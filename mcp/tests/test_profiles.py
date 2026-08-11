import tempfile
import unittest
from pathlib import Path

from profiles import (
    MAX_PATCH_SOURCE_LENGTH,
    ProfileStore,
    ProfileValidationError,
    validate_profile,
)


def set_profile(auto_apply: bool = False) -> dict:
    return {
        "version": 1,
        "autoApply": auto_apply,
        "operations": [
            {
                "command": "component/set_value",
                "selector": {
                    "scene": "Game",
                    "path": "/Player[0]",
                },
                "componentName": "PlayerController",
                "memberName": "health",
                "value": "100",
            }
        ],
    }


class ProfileStoreTests(unittest.TestCase):
    def test_round_trip_and_auto_apply_gating(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            store = ProfileStore(Path(directory))
            store.save("manual", set_profile(False))
            store.save("automatic", set_profile(True))
            store.set_active("manual", True)
            store.set_active("automatic", True)

            self.assertEqual(store.auto_apply_names(), ["automatic"])
            self.assertEqual(
                store.get("automatic")["operations"][0]["value"],
                "100",
            )

    def test_path_traversal_name_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            store = ProfileStore(Path(directory))
            with self.assertRaises(ProfileValidationError):
                store.save("../outside", set_profile())
            self.assertFalse((Path(directory).parent / "outside.json").exists())

    def test_selector_requires_stable_identity(self) -> None:
        profile = set_profile()
        profile["operations"][0]["selector"] = {"name": "Player"}
        with self.assertRaisesRegex(ProfileValidationError, "stable selector"):
            validate_profile(profile)

    def test_patch_source_limit_is_enforced(self) -> None:
        profile = {
            "version": 1,
            "autoApply": False,
            "operations": [
                {
                    "command": "mod:patch_method",
                    "targetClass": "Player",
                    "targetMethod": "Update",
                    "patchType": "prefix",
                    "patchCode": "x" * (MAX_PATCH_SOURCE_LENGTH + 1),
                }
            ],
        }
        with self.assertRaisesRegex(ProfileValidationError, "exceeds"):
            validate_profile(profile)


if __name__ == "__main__":
    unittest.main()
