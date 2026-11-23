from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import subprocess
from tempfile import TemporaryDirectory
import unittest

import grpc
from google.protobuf import json_format
from google.protobuf.struct_pb2 import Struct

from retroplc_zephyr.transport import SERVICE, make_server


class WestTransportTests(unittest.TestCase):
    def setUp(self):
        temporary = TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.workspace = self.root / ".zephyr" / "workspace"
        metadata = self.workspace / ".west"
        metadata.mkdir(parents=True)
        (metadata / "config").write_text("[manifest]\npath = runtime\nfile = custom.yml\n")
        self.manifest = self.workspace / "runtime" / "custom.yml"
        self.manifest.parent.mkdir()
        self.manifest.write_text(
            "manifest:\n  projects:\n"
            "    - name: zephyr\n      url: https://example.invalid/zephyr\n"
            "      path: sources/zephyr\n      revision: test-revision\n"
        )
        self.zephyr = self.workspace / "sources" / "zephyr"
        self.zephyr.mkdir(parents=True)
        subprocess.run(["git", "init", "--quiet", str(self.zephyr)], check=True)
        server, port = make_server(self.root)
        server.start()
        self.addCleanup(lambda: server.stop(grace=0).wait())
        self.channel = grpc.insecure_channel(f"127.0.0.1:{port}")
        self.addCleanup(self.channel.close)

    def rpc(self, method, **request):
        call = self.channel.unary_unary(
            f"/{SERVICE}/{method}", request_serializer=Struct.SerializeToString,
            response_deserializer=Struct.FromString)
        return json_format.MessageToDict(call(json_format.ParseDict(request, Struct()), timeout=3))

    def test_discovery_and_validation(self):
        info = self.rpc("GetWorkspaceInfo")
        self.assertEqual(info, self.rpc("ValidateWorkspace", workspaceDirectory=str(self.workspace)))
        self.assertEqual(info["directory"], str(self.workspace))
        self.assertEqual(info["manifestFile"], str(self.manifest))
        self.assertEqual(info["manifestRepository"], str(self.manifest.parent))
        self.assertEqual(info["zephyrDirectory"], str(self.zephyr))
        self.assertTrue(info["zephyrCloned"])

    def test_concurrent_workspace_queries(self):
        with ThreadPoolExecutor(max_workers=8) as workers:
            infos = list(workers.map(lambda _: self.rpc("GetWorkspaceInfo"), range(16)))
        self.assertTrue(all(info["zephyrCloned"] for info in infos))

    def test_projects(self):
        selected = self.rpc("GetProjects", names=["zephyr"])["projects"]
        self.assertEqual(len(selected), 1)
        self.assertEqual(selected[0]["revision"], "test-revision")
        self.assertEqual(selected[0]["path"], "sources/zephyr")
        self.assertEqual({item["name"] for item in self.rpc("GetProjects")["projects"]}, {"manifest", "zephyr"})

    def test_west_config(self):
        self.assertEqual(self.rpc("GetWestConfig", options=["manifest.file", "manifest.unknown"])["values"],
                         {"manifest.file": "custom.yml", "manifest.unknown": None})

    def test_missing_workspace(self):
        with self.assertRaises(grpc.RpcError) as error:
            self.rpc("ValidateWorkspace", workspaceDirectory="missing")
        self.assertEqual(error.exception.code(), grpc.StatusCode.NOT_FOUND)

    def test_missing_manifest(self):
        self.manifest.unlink()
        with self.assertRaises(grpc.RpcError) as error:
            self.rpc("ValidateWorkspace")
        self.assertEqual(error.exception.code(), grpc.StatusCode.FAILED_PRECONDITION)
        self.assertIn(str(self.manifest), error.exception.details())

    def test_unsupported_methods(self):
        for name in ("ResolveConfiguration", "ResolveTerminal", "ListBoards", "ValidateBoard", "ValidateSdk"):
            with self.subTest(method=name), self.assertRaises(grpc.RpcError) as error:
                self.rpc(name)
            self.assertEqual(error.exception.code(), grpc.StatusCode.UNIMPLEMENTED)


if __name__ == "__main__":
    unittest.main()
