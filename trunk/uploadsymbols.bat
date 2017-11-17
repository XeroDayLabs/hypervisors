cd %1
set arg=%~dpnx2
REM Remove any trailing backslash
IF %arg:~-1%==\ set arg=%arg:~0,-1%
%SystemRoot%\system32\WindowsPowerShell\v1.0\powershell.exe -ExecutionPolicy Bypass -File uploadsymbols.ps1 "%arg%"

