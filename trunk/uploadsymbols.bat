cd %1
arg=%%~dpnx2
%SystemRoot%\system32\WindowsPowerShell\v1.0\powershell.exe -ExecutionPolicy Bypass -File uploadsymbols.ps1 %arg
