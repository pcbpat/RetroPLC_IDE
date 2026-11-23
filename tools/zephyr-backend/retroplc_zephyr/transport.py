"""gRPC transport: documented west Python APIs; no CLI output parsing."""
from __future__ import annotations

import argparse
from concurrent import futures
import json
import logging
import os
from pathlib import Path
import sys
import threading

import grpc
from google.protobuf import json_format
from google.protobuf.struct_pb2 import Struct
from west.configuration import MalformedConfig
from west.manifest import MalformedManifest, ManifestImportFailed, ManifestVersionError
from west.util import WestNotFound

from .workspace import workspace_info, projects, west_config

SERVICE = "retroplc.zephyr.v1.ZephyrEnvironment"


def _decode(value: Struct) -> dict:
    return json_format.MessageToDict(value, preserving_proto_field_name=True)


def _encode(payload: dict) -> Struct:
    message = Struct()
    json_format.ParseDict(payload, message)
    return message


class Backend:
    def __init__(self, root: Path):
        self.root = root.resolve()

    def _workspace(self, request: dict) -> str:
        name = request.get("workspaceDirectory", ".zephyr/workspace")
        if not isinstance(name, str):
            raise ValueError("workspaceDirectory must be a string")
        return str((self.root / name).resolve())

    def validate_workspace(self, request: dict) -> dict:
        return workspace_info(self._workspace(request))

    def get_workspace_info(self, request: dict) -> dict:
        return workspace_info(self._workspace(request))

    def get_projects(self, request: dict) -> dict:
        names = request.get("names", [])
        if not isinstance(names, list) or any(not isinstance(value, str) for value in names):
            raise ValueError("names must be an array of project identifiers")
        return projects(self._workspace(request), names)

    def get_west_config(self, request: dict) -> dict:
        return west_config(self._workspace(request), request.get("options", []))

    @staticmethod
    def unsupported(request: dict) -> dict:
        raise NotImplementedError(
            "Not exposed by the documented west Python API. "
            "Use Zephyr's separately versioned interfaces or implement this in the IDE."
        )


def make_server(root: Path, host: str = "127.0.0.1"):
    if host != "127.0.0.1":
        raise ValueError("Only IPv4 loopback binding is supported")
    backend = Backend(root)
    functions = {
        "ValidateWorkspace": backend.validate_workspace,
        "GetWorkspaceInfo": backend.get_workspace_info,
        "GetProjects": backend.get_projects,
        "GetWestConfig": backend.get_west_config,
        "ResolveConfiguration": backend.unsupported,
        "ListBoards": backend.unsupported,
        "ValidateBoard": backend.unsupported,
        "ValidateSdk": backend.unsupported,
        "ResolveTerminal": backend.unsupported,
    }

    def wrap(function):
        def run(message, context):
            try:
                return _encode(function(_decode(message)))
            except NotImplementedError as exc:
                context.abort(grpc.StatusCode.UNIMPLEMENTED, str(exc))
            except (ValueError, TypeError, KeyError) as exc:
                context.abort(grpc.StatusCode.INVALID_ARGUMENT, str(exc))
            except WestNotFound:
                context.abort(grpc.StatusCode.NOT_FOUND,
                              f"No west workspace at '{backend._workspace(_decode(message))}'. Run setup.sh or check the workspace path.")
            except (MalformedConfig, MalformedManifest, ManifestImportFailed, ManifestVersionError) as exc:
                context.abort(grpc.StatusCode.FAILED_PRECONDITION, f"Unable to load the west manifest: {exc}")
            except OSError as exc:
                context.abort(grpc.StatusCode.NOT_FOUND, str(exc))
            except Exception:
                logging.exception("west API request failed")
                context.abort(grpc.StatusCode.FAILED_PRECONDITION,
                              "Unable to query west workspace; check backend logs")
        return run

    handlers = {
        name: grpc.unary_unary_rpc_method_handler(
            wrap(fn), request_deserializer=Struct.FromString,
            response_serializer=Struct.SerializeToString)
        for name, fn in functions.items()
    }
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=4),
                         options=(("grpc.so_reuseport", 0),))
    server.add_generic_rpc_handlers((grpc.method_handlers_generic_handler(SERVICE, handlers),))
    port = server.add_insecure_port(f"{host}:0")
    if port == 0:
        raise RuntimeError("Unable to bind gRPC loopback endpoint")
    return server, port


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--endpoint-file", type=Path, required=True)
    parser.add_argument("--exit-on-stdin-close", action="store_true")
    args = parser.parse_args()
    logging.basicConfig(stream=sys.stderr, level=logging.WARNING)
    if not args.root.is_dir():
        parser.error("Installation root does not exist")
    server, port = make_server(args.root)
    server.start()
    endpoint = args.endpoint_file
    endpoint.parent.mkdir(parents=True, exist_ok=True)
    temporary = endpoint.with_name(endpoint.name + ".tmp")
    temporary.write_text(json.dumps({"host": "127.0.0.1", "port": port,
                                     "pid": os.getpid()}), encoding="utf-8")
    temporary.replace(endpoint)
    if args.exit_on_stdin_close:
        def stop_on_close():
            while os.read(sys.stdin.fileno(), 4096):
                pass
            server.stop(grace=1)
        threading.Thread(target=stop_on_close, daemon=True).start()
    try:
        server.wait_for_termination()
    except KeyboardInterrupt:
        server.stop(grace=2)
    finally:
        endpoint.unlink(missing_ok=True)


if __name__ == "__main__":
    main()
