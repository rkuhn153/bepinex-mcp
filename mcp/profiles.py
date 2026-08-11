"""Validated, translator-owned persistent mod profiles."""

from __future__ import annotations

import json
import os
import re
from copy import deepcopy
from pathlib import Path
from typing import Any

PROFILE_VERSION = 1
MAX_PROFILE_OPERATIONS = 100
MAX_PATCH_SOURCE_LENGTH = 48 * 1024
PROFILE_COMMANDS = {
    "component/set_value",
    "component/call_method",
    "watch/member",
    "mod:patch_method",
}
BATCH_COMMANDS = {
    "component/set_value",
    "component/call_method",
}
_SAFE_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")


class ProfileValidationError(ValueError):
    pass


def default_profiles_dir() -> Path:
    override = os.environ.get("UNITY_MCP_PROFILES_DIR")
    if override:
        return Path(override).expanduser()
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise ProfileValidationError(
            "LOCALAPPDATA is unavailable; pass --profiles-dir or set "
            "UNITY_MCP_PROFILES_DIR."
        )
    return Path(local_app_data) / "unity-mcp-translator" / "profiles"


def validate_profile_name(name: str) -> str:
    normalized = name.strip()
    if (
        not _SAFE_NAME.fullmatch(normalized)
        or normalized in {".", "..", "_active"}
        or normalized.endswith(".")
    ):
        raise ProfileValidationError(
            "Profile names must be 1-64 characters, start with a letter or "
            "digit, and contain only letters, digits, '.', '_' or '-'."
        )
    return normalized


def validate_selector(selector: Any) -> dict[str, str]:
    if not isinstance(selector, dict):
        raise ProfileValidationError("A stable selector object is required.")
    allowed = {"scene", "path", "component", "name"}
    unknown = set(selector) - allowed
    if unknown:
        raise ProfileValidationError(
            f"Unknown selector field(s): {', '.join(sorted(unknown))}."
        )
    normalized: dict[str, str] = {}
    for key, value in selector.items():
        if not isinstance(value, str) or not value.strip():
            raise ProfileValidationError(
                f"Selector field '{key}' must be a non-empty string."
            )
        normalized[key] = value.strip()
    if not normalized.get("path") and not (
        normalized.get("component") and normalized.get("name")
    ):
        raise ProfileValidationError(
            "A stable selector requires a hierarchy path or both component and name."
        )
    return normalized


def validate_profile(profile: Any) -> dict[str, Any]:
    if not isinstance(profile, dict):
        raise ProfileValidationError("A profile must be a JSON object.")
    unknown = set(profile) - {"version", "name", "autoApply", "operations"}
    if unknown:
        raise ProfileValidationError(
            f"Unknown profile field(s): {', '.join(sorted(unknown))}."
        )
    if profile.get("version") != PROFILE_VERSION:
        raise ProfileValidationError(
            f"Profile version must be {PROFILE_VERSION}."
        )
    if not isinstance(profile.get("autoApply", False), bool):
        raise ProfileValidationError("'autoApply' must be true or false.")
    operations = profile.get("operations")
    if not isinstance(operations, list) or not (
        1 <= len(operations) <= MAX_PROFILE_OPERATIONS
    ):
        raise ProfileValidationError(
            f"'operations' must contain 1-{MAX_PROFILE_OPERATIONS} items."
        )

    validated_operations = [
        _validate_operation(operation, index)
        for index, operation in enumerate(operations)
    ]
    result = {
        "version": PROFILE_VERSION,
        "autoApply": profile.get("autoApply", False),
        "operations": validated_operations,
    }
    if isinstance(profile.get("name"), str):
        result["name"] = validate_profile_name(profile["name"])
    return result


def _validate_operation(operation: Any, index: int) -> dict[str, Any]:
    if not isinstance(operation, dict):
        raise ProfileValidationError(f"Operation {index} must be an object.")
    command = operation.get("command")
    if command not in PROFILE_COMMANDS:
        raise ProfileValidationError(
            f"Operation {index} command must be one of: "
            f"{', '.join(sorted(PROFILE_COMMANDS))}."
        )
    normalized = deepcopy(operation)
    normalized["command"] = command

    if command in {
        "component/set_value",
        "component/call_method",
        "watch/member",
    }:
        normalized["selector"] = validate_selector(operation.get("selector"))
        _require_text(operation, "componentName", index)

    if command == "component/set_value":
        _require_text(operation, "memberName", index)
        if "value" not in operation:
            raise ProfileValidationError(
                f"Operation {index} requires 'value'."
            )
        normalized["value"] = str(operation["value"])
    elif command == "component/call_method":
        _require_text(operation, "methodName", index)
        args = operation.get("args", [])
        if not isinstance(args, list):
            raise ProfileValidationError(
                f"Operation {index} 'args' must be a JSON array."
            )
        normalized["args"] = args
    elif command == "watch/member":
        _require_text(operation, "memberName", index)
        interval = operation.get("intervalMs", 500)
        if not isinstance(interval, int) or not 100 <= interval <= 60_000:
            raise ProfileValidationError(
                f"Operation {index} intervalMs must be 100-60000."
            )
        normalized["intervalMs"] = interval
    elif command == "mod:patch_method":
        _require_text(operation, "targetClass", index)
        _require_text(operation, "targetMethod", index)
        patch_type = str(operation.get("patchType", "prefix")).lower()
        if patch_type not in {"prefix", "postfix", "transpiler", "finalizer"}:
            raise ProfileValidationError(
                f"Operation {index} has an invalid patchType."
            )
        source = operation.get("patchCode")
        if not isinstance(source, str) or not source.strip():
            raise ProfileValidationError(
                f"Operation {index} requires non-empty patchCode."
            )
        if len(source) > MAX_PATCH_SOURCE_LENGTH:
            raise ProfileValidationError(
                f"Operation {index} patchCode exceeds "
                f"{MAX_PATCH_SOURCE_LENGTH} characters."
            )
        normalized["patchType"] = patch_type
        normalized["parameterTypes"] = str(operation.get("parameterTypes", ""))

    registration_id = operation.get("registrationId")
    if registration_id is not None:
        if (
            not isinstance(registration_id, str)
            or not re.fullmatch(r"[A-Za-z0-9._-]{1,128}", registration_id)
        ):
            raise ProfileValidationError(
                f"Operation {index} has an invalid registrationId."
            )
    return normalized


def _require_text(operation: dict[str, Any], field: str, index: int) -> str:
    value = operation.get(field)
    if not isinstance(value, str) or not value.strip():
        raise ProfileValidationError(
            f"Operation {index} requires non-empty '{field}'."
        )
    return value.strip()


class ProfileStore:
    def __init__(self, directory: Path | str):
        self.directory = Path(directory).expanduser().resolve()
        self.directory.mkdir(parents=True, exist_ok=True)
        self._active_path = self.directory / "_active.json"

    def save(self, name: str, profile: Any) -> dict[str, Any]:
        safe_name = validate_profile_name(name)
        validated = validate_profile(profile)
        validated["name"] = safe_name
        self._write_json(self._profile_path(safe_name), validated)
        return validated

    def get(self, name: str) -> dict[str, Any]:
        path = self._profile_path(validate_profile_name(name))
        if not path.is_file():
            raise FileNotFoundError(f"Profile '{name}' was not found.")
        with path.open("r", encoding="utf-8") as handle:
            return validate_profile(json.load(handle))

    def list(self) -> list[dict[str, Any]]:
        active = self.active_names()
        result: list[dict[str, Any]] = []
        for path in sorted(self.directory.glob("*.json")):
            if path.name == self._active_path.name:
                continue
            name = path.stem
            try:
                profile = self.get(name)
                result.append(
                    {
                        "name": name,
                        "autoApply": profile["autoApply"],
                        "active": name in active,
                        "operationCount": len(profile["operations"]),
                    }
                )
            except (OSError, ValueError, json.JSONDecodeError):
                result.append({"name": name, "invalid": True, "active": False})
        return result

    def delete(self, name: str) -> None:
        safe_name = validate_profile_name(name)
        path = self._profile_path(safe_name)
        if not path.is_file():
            raise FileNotFoundError(f"Profile '{name}' was not found.")
        path.unlink()
        active = self.active_names()
        if safe_name in active:
            active.remove(safe_name)
            self._write_active(active)

    def set_active(self, name: str, active: bool) -> list[str]:
        safe_name = validate_profile_name(name)
        self.get(safe_name)
        names = self.active_names()
        if active:
            names.add(safe_name)
        else:
            names.discard(safe_name)
        self._write_active(names)
        return sorted(names)

    def active_names(self) -> set[str]:
        if not self._active_path.is_file():
            return set()
        try:
            with self._active_path.open("r", encoding="utf-8") as handle:
                value = json.load(handle)
            if not isinstance(value, list):
                return set()
            return {
                validate_profile_name(item)
                for item in value
                if isinstance(item, str)
            }
        except (OSError, ValueError, json.JSONDecodeError):
            return set()

    def auto_apply_names(self) -> list[str]:
        result: list[str] = []
        for name in sorted(self.active_names()):
            try:
                if self.get(name).get("autoApply", False):
                    result.append(name)
            except (OSError, ValueError, json.JSONDecodeError):
                continue
        return result

    def _profile_path(self, name: str) -> Path:
        candidate = (self.directory / f"{name}.json").resolve()
        if candidate.parent != self.directory:
            raise ProfileValidationError("Profile path escaped the profile directory.")
        return candidate

    def _write_active(self, names: set[str]) -> None:
        self._write_json(self._active_path, sorted(names))

    @staticmethod
    def _write_json(path: Path, value: Any) -> None:
        temporary = path.with_suffix(path.suffix + ".tmp")
        with temporary.open("w", encoding="utf-8", newline="\n") as handle:
            json.dump(value, handle, indent=2, ensure_ascii=False)
            handle.write("\n")
        temporary.replace(path)
