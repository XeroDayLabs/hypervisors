$srcDir = $args[0]
$rand = Get-Random
$tmpDir = "\\applejack\incomingSymbols\" + $env.computername + $rand + $SrcDir.Replace("\", "_").Replace(":", "_").Replace(" ", "_")
echo $tmpDir
New-Item $tmpDir -type Directory
Copy-Item $srcDir\*.pdb $tmpDir