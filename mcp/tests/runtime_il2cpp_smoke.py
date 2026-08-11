"""Destructive-only-to-self-test runtime smoke test for Bang Bang Barrage."""

from __future__ import annotations

import asyncio
import json
import sys
import tempfile
import threading
import time
from pathlib import Path

import httpx

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import ModdersHelperApp as app
from profiles import ProfileStore

BASE_URL = "http://localhost:8080/mcp"


def get(endpoint: str, params: dict | None = None) -> dict | list:
    response = httpx.get(
        f"{BASE_URL}/{endpoint}",
        params=params,
        timeout=30,
    )
    response.raise_for_status()
    return response.json()


def post_batch(operations: list[dict], stop_on_error: bool = False) -> dict:
    response = httpx.post(
        f"{BASE_URL}/batch",
        json={"operations": operations, "stopOnError": stop_on_error},
        timeout=30,
    )
    response.raise_for_status()
    return response.json()


def wait_for_event(registration_id: str, timeout: float = 8.0) -> dict:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        remaining = max(0.05, deadline - time.monotonic())
        event = app.event_queue.get(timeout=remaining)
        if event.get("registrationId") == registration_id:
            return event
    raise AssertionError(f"Event '{registration_id}' was not received.")


def wait_for_member(
    object_id: int,
    component: str,
    member: str,
    expected_fragment: str,
    timeout: float = 8.0,
) -> dict:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = get(
            "component/get_member",
            {
                "id": object_id,
                "componentName": component,
                "memberName": member,
            },
        )
        if expected_fragment in value["value"]:
            return value
        time.sleep(0.2)
    raise AssertionError(
        f"{component}.{member} did not contain '{expected_fragment}'."
    )


def main() -> None:
    capabilities = get("system/capabilities")
    assert capabilities["protocolVersion"] == "2.0"
    assert capabilities["runtime"] == "il2cpp"
    assert get("system/ping")["status"] == "ok"
    self_test_id = capabilities["selfTestObjectId"]

    search = get(
        "scene/search_gameobjects",
        {"name": "__BepInExMCP_IL2CPP_SelfTest", "limit": 10},
    )
    target = next(item for item in search["items"] if item["id"] == self_test_id)
    selector = {
        "scene": target["scene"],
        "path": target["path"],
        "name": target["name"],
    }
    resolved = get(
        "scene/resolve_selector",
        {"selector": json.dumps(selector, separators=(",", ":"))},
    )
    assert resolved["id"] == self_test_id

    snapshot = get(
        "scene/hierarchy_snapshot",
        {"id": self_test_id, "depth": 2, "maxNodes": 20},
    )
    assert snapshot[0]["id"] == self_test_id
    assert any(child["name"] == "Child" for child in snapshot[0]["children"])

    components = get("gameobject/inspect_components", {"id": self_test_id})
    transform_type = next(name for name in components if name.endswith("Transform"))
    test_component = next(
        name for name in components if name.endswith("BridgeSelfTestComponent")
    )
    original_position = get(
        "component/get_member",
        {
            "id": self_test_id,
            "componentName": transform_type,
            "memberName": "localPosition",
        },
    )["value"]

    type_matches = get(
        "type/search",
        {"query": "BridgeSelfTestComponent", "offset": 0, "limit": 10},
    )
    assert any(item["fullName"] == test_component for item in type_matches)
    description = get(
        "type/describe",
        {"typeName": test_component, "offset": 0, "limit": 100},
    )
    assert any(method["name"] == "Echo" for method in description["methods"])
    assert get("network/diagnostics", {"id": self_test_id})["instanceId"] == self_test_id

    batch = post_batch(
        [
            {
                "id": "read",
                "command": "component/get_member",
                "parameters": {
                    "id": self_test_id,
                    "componentName": transform_type,
                    "memberName": "childCount",
                },
            },
            {
                "id": "call",
                "command": "component/call_method",
                "parameters": {
                    "id": self_test_id,
                    "componentName": test_component,
                    "methodName": "Add",
                    "args": "[2,3]",
                },
            },
        ]
    )
    assert [item["id"] for item in batch["results"]] == ["read", "call"]
    assert all(item["ok"] for item in batch["results"])
    stopped = post_batch(
        [
            {"id": "bad", "command": "not/allowed", "parameters": {}},
            {
                "id": "skipped",
                "command": "network/diagnostics",
                "parameters": {"id": self_test_id},
            },
        ],
        stop_on_error=True,
    )
    assert [item["id"] for item in stopped["results"]] == ["bad"]

    too_many = [
        {
            "id": str(index),
            "command": "network/diagnostics",
            "parameters": {"id": self_test_id},
        }
        for index in range(101)
    ]
    response = httpx.post(
        f"{BASE_URL}/batch",
        json={"operations": too_many},
        timeout=30,
    )
    assert response.status_code == 400
    response = httpx.post(
        f"{BASE_URL}/batch",
        content=json.dumps(
            {
                "operations": [
                    {
                        "id": "large",
                        "command": "network/diagnostics",
                        "parameters": {"id": self_test_id},
                    }
                ],
                "padding": "x" * (300 * 1024),
            }
        ),
        headers={"content-type": "application/json"},
        timeout=30,
    )
    assert response.status_code == 413

    with tempfile.TemporaryDirectory() as directory:
        app.profile_store = ProfileStore(Path(directory))
        webhook_thread = threading.Thread(
            target=app.start_webhook_server,
            daemon=True,
        )
        webhook_thread.start()
        profile_thread = threading.Thread(
            target=app.profile_auto_apply_worker,
            daemon=True,
        )
        profile_thread.start()
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            try:
                if httpx.get("http://localhost:8081/unknown", timeout=0.3).status_code:
                    break
            except httpx.HTTPError:
                time.sleep(0.1)

        try:
            try:
                get("watch/remove", {"registrationId": "runtime-watch"})
            except httpx.HTTPStatusError:
                pass
            watch = get(
                "watch/member",
                {
                    "selector": json.dumps(selector, separators=(",", ":")),
                    "componentName": transform_type,
                    "memberName": "localPosition",
                    "intervalMs": 100,
                    "registrationId": "runtime-watch",
                },
            )
            assert watch["id"] == "runtime-watch"
            assert any(
                item["id"] == "runtime-watch" for item in get("watch/list")
            )
            get(
                "component/set_value",
                {
                    "id": self_test_id,
                    "componentName": transform_type,
                    "memberName": "localPosition",
                    "value": "1,2,3",
                },
            )
            event = wait_for_event("runtime-watch")
            assert event["kind"] == "watch.changed"
            get("watch/remove", {"registrationId": "runtime-watch"})

            patch_source = (
                "public class DynamicPatcher { "
                "public static void Postfix(ref int __result) { __result += 1; } }"
            )
            patch = get(
                "mod:patch_method",
                {
                    "targetClass": test_component,
                    "targetMethod": "Echo",
                    "parameterTypes": "System.Int32",
                    "patchType": "postfix",
                    "patchCode": patch_source,
                    "registrationId": "runtime-patch",
                },
            )
            assert patch["id"] == "runtime-patch"
            assert any(
                item["id"] == "runtime-patch" for item in get("mod:list_patches")
            )
            patched_call = get(
                "component/call_method",
                {
                    "id": self_test_id,
                    "componentName": test_component,
                    "methodName": "Echo",
                    "args": "[3]",
                },
            )
            assert "ReturnValue: 4" in patched_call["message"]
            get("mod:remove_patch", {"registrationId": "runtime-patch"})
            unpatched_call = get(
                "component/call_method",
                {
                    "id": self_test_id,
                    "componentName": test_component,
                    "methodName": "Echo",
                    "args": "[3]",
                },
            )
            assert "ReturnValue: 3" in unpatched_call["message"]

            profile = {
                "version": 1,
                "autoApply": True,
                "operations": [
                    {
                        "command": "component/set_value",
                        "selector": selector,
                        "componentName": transform_type,
                        "memberName": "localPosition",
                        "value": "2,0,0",
                    }
                ],
            }
            app.profile_store.save("runtime-auto", profile)
            app.profile_store.set_active("runtime-auto", True)
            asyncio.run(app._apply_profile_name("runtime-auto"))
            wait_for_member(
                self_test_id,
                transform_type,
                "localPosition",
                "2.00",
            )
            get(
                "component/set_value",
                {
                    "id": self_test_id,
                    "componentName": transform_type,
                    "memberName": "localPosition",
                    "value": "9,0,0",
                },
            )
            httpx.post(
                "http://localhost:8081/event",
                json={
                    "kind": "scene.changed",
                    "registrationId": "runtime-scene",
                    "timestampUnixMs": int(time.time() * 1000),
                },
                timeout=5,
            ).raise_for_status()
            wait_for_member(
                self_test_id,
                transform_type,
                "localPosition",
                "2.00",
            )
        finally:
            try:
                get("watch/remove", {"registrationId": "runtime-watch"})
            except httpx.HTTPStatusError:
                pass
            try:
                get("mod:remove_patch", {"registrationId": "runtime-patch"})
            except httpx.HTTPStatusError:
                pass
            get(
                "component/set_value",
                {
                    "id": self_test_id,
                    "componentName": transform_type,
                    "memberName": "localPosition",
                    "value": original_position,
                },
            )

    print("IL2CPP_RUNTIME_SMOKE_OK")


if __name__ == "__main__":
    main()
