"""Проверяет, что _runtime_path() и _detect_git_sha() работают как ожидается."""

import os
from pathlib import Path


def test_runtime_path_uses_env_var(app_module, tmp_path):
    target = tmp_path / "custom-config.json"
    os.environ["CUSTOM_VAR"] = str(target)
    try:
        result = app_module._runtime_path("CUSTOM_VAR", Path("/should/not/be/used"))
        assert result == target.resolve()
    finally:
        os.environ.pop("CUSTOM_VAR", None)


def test_runtime_path_falls_back_to_default(app_module, tmp_path):
    default = tmp_path / "default.json"
    os.environ.pop("UNSET_VAR", None)
    result = app_module._runtime_path("UNSET_VAR", default)
    assert result == default


def test_runtime_path_treats_empty_as_unset(app_module, tmp_path):
    default = tmp_path / "default.json"
    os.environ["EMPTY_VAR"] = "   "
    try:
        result = app_module._runtime_path("EMPTY_VAR", default)
        assert result == default
    finally:
        os.environ.pop("EMPTY_VAR", None)


def test_detect_git_sha_returns_string(app_module):
    result = app_module._detect_git_sha()
    assert isinstance(result, str)
    assert result == "unknown" or len(result) >= 7
