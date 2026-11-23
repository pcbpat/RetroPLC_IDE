"""Workspace information through the documented west Python API only."""
from __future__ import annotations

from pathlib import Path
from threading import Lock
from typing import Any

from west.configuration import Configuration
from west.manifest import Manifest
from west.util import west_topdir, west_dir

_manifest_load = Lock()


def _workspace(workspace: str) -> tuple[str, Manifest, Configuration]:
    if not workspace:
        raise ValueError("workspaceDirectory is required")
    requested = Path(workspace).resolve()
    topdir = west_topdir(start=requested, fall_back=False)
    if Path(topdir).resolve() != requested:
        raise ValueError("Expected a workspace root, not a nested project directory")
    config = Configuration(topdir=topdir)
    # The west manifest loader uses a schema validator with shared state.
    with _manifest_load:
        manifest = Manifest.from_topdir(topdir=topdir, config=config)
    return topdir, manifest, config


def workspace_info(workspace: str) -> dict[str, Any]:
    topdir, manifest, config = _workspace(workspace)
    try:
        zephyr, = manifest.get_projects(["zephyr"], allow_paths=False)
    except ValueError as exc:
        raise ValueError("The west manifest does not contain a project named 'zephyr'") from exc
    manifest_project = manifest.projects[0]
    return {
        "directory": str(Path(topdir).resolve()),
        "configFile": str(Path(west_dir(start=topdir)) / "config"),
        "manifestFile": manifest.abspath,
        "manifestRepository": manifest_project.abspath,
        "zephyrDirectory": zephyr.abspath,
        "zephyrCloned": zephyr.is_cloned(),
        "manifestPathSetting": config.get("manifest.path"),
        "manifestFileSetting": config.get("manifest.file"),
    }


def projects(workspace: str, names: list[str] | None = None) -> dict[str, Any]:
    topdir, manifest, _ = _workspace(workspace)
    selected = manifest.get_projects(names or [])
    return {
        "workspaceDirectory": str(Path(topdir).resolve()),
        "projects": [{
            "name": project.name,
            "directory": project.abspath,
            "path": str(project.path) if project.path is not None else None,
            "revision": project.revision,
            "cloned": project.is_cloned() if project.name != "manifest" else None,
        } for project in selected],
    }


def west_config(workspace: str, options: list[str]) -> dict[str, Any]:
    _, _, config = _workspace(workspace)
    if not isinstance(options, list) or any(not isinstance(option, str) for option in options):
        raise ValueError("options must be a list of west configuration option names")
    return {"values": {option: config.get(option) for option in options}}
