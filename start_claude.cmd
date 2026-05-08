@echo off
REM Resume the last Claude Code session for this project with bypass permissions.
REM Combines --continue (restore prior session context) with --dangerously-skip-permissions.
cd /d "%~dp0"
claude --continue --dangerously-skip-permissions %*
