import json
import unittest

import ModdersHelperApp as app
from bridge_client import BridgeError
from profiles import ProfileValidationError, validate_profile


class FakeBridgeClient:
    def __init__(self, fail_batch: bool = False):
        self.fail_batch = fail_batch
        self.batches = []
        self.get_calls = []

    async def get(self, endpoint, params=None):
        self.get_calls.append((endpoint, params))
        if endpoint == "system/capabilities":
            return json.dumps(
                {
                    "runtime": "il2cpp",
                    "protocolVersion": "2.0",
                    "patchTypes": ["prefix", "postfix"],
                    "limits": {"maxBatchOperations": 100},
                }
            )
        if endpoint == "scene/resolve_selector":
            return json.dumps({"id": 42})
        raise AssertionError(f"Unexpected GET endpoint: {endpoint}")

    async def post_batch(self, operations, stop_on_error=False):
        self.batches.append((operations, stop_on_error))
        if self.fail_batch:
            return json.dumps(
                {
                    "results": [
                        {
                            "id": operations[0]["id"],
                            "ok": False,
                            "error": {"error": "failed", "code": "operation_failed"},
                        }
                    ]
                }
            )
        return json.dumps(
            {
                "results": [
                    {"id": operation["id"], "ok": True, "result": {"status": "ok"}}
                    for operation in operations
                ]
            }
        )


class ProfileApplicationTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.original_client = app.bridge_client

    async def asyncTearDown(self):
        app.bridge_client = self.original_client

    async def test_selectors_are_resolved_and_batch_order_is_preserved(self):
        fake = FakeBridgeClient()
        app.bridge_client = fake
        profile = validate_profile(
            {
                "version": 1,
                "autoApply": False,
                "operations": [
                    {
                        "command": "component/set_value",
                        "selector": {"scene": "Game", "path": "/Player[0]"},
                        "componentName": "Player",
                        "memberName": "health",
                        "value": "100",
                    },
                    {
                        "command": "component/call_method",
                        "selector": {"scene": "Game", "path": "/Player[0]"},
                        "componentName": "Player",
                        "methodName": "Heal",
                        "args": [5],
                    },
                ],
            }
        )

        result = await app._apply_profile_data(profile, "combat")

        self.assertTrue(result["ok"])
        operations, stop_on_error = fake.batches[0]
        self.assertTrue(stop_on_error)
        self.assertEqual(
            [operation["id"] for operation in operations],
            ["combat-0", "combat-1"],
        )
        self.assertEqual(operations[0]["parameters"]["id"], 42)
        self.assertEqual(operations[1]["parameters"]["args"], "[5]")

    async def test_batch_failure_stops_profile_application(self):
        app.bridge_client = FakeBridgeClient(fail_batch=True)
        profile = validate_profile(
            {
                "version": 1,
                "autoApply": False,
                "operations": [
                    {
                        "command": "component/set_value",
                        "selector": {"path": "/Player[0]"},
                        "componentName": "Player",
                        "memberName": "health",
                        "value": "100",
                    }
                ],
            }
        )

        with self.assertRaises(BridgeError):
            await app._apply_profile_data(profile, "failure")

    async def test_runtime_patch_validation_rejects_il2cpp_transpiler(self):
        app.bridge_client = FakeBridgeClient()
        profile = validate_profile(
            {
                "version": 1,
                "autoApply": False,
                "operations": [
                    {
                        "command": "mod:patch_method",
                        "targetClass": "Player",
                        "targetMethod": "Update",
                        "patchType": "transpiler",
                        "patchCode": "public class DynamicPatcher { }",
                    }
                ],
            }
        )

        with self.assertRaisesRegex(ProfileValidationError, "does not support"):
            await app._validate_profile_runtime(profile)


if __name__ == "__main__":
    unittest.main()
