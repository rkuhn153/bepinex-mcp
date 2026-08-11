@echo off
title Unity MCP - Gemini Spark (KEEP OPEN)
cd /d "%~dp0"
echo.
echo Starting Gemini Spark bridge - leave this window open.
echo.
"C:\Python313\python.exe" -u "%~dp0run_gemini_spark.py"
echo.
echo Exited. Press any key to close.
pause >nul
