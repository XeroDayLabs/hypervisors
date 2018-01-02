$srcDir = $args[0]

# Do some source indexing.
$tmpfilename=$env:TEMP + '\tmp.ini'
$baseURL="http://files.xd.lan:7990/projects/XD/repos/hypervisors/browse"
$commitid=(git rev-parse HEAD)
Copy-Item tmp-pre.ini $tmpfilename

$tmpIni = [System.io.File]::Open($tmpfilename, 'Append', 'Write', 'None')

# First off, do a 'git ls-files' to get hashes:
$lines=(git ls-files -s).split("`n");
$enc=[system.Text.Encoding]::UTF8
ForEach($line in  $lines)
{
  $hash = $line.Substring(7,40);
  $filename = $line.Substring(50);
  $filename = $filename.Replace('/', '\');
  $tempfile=$hash + '\' + $filename
  $url=$baseURL + '\' + $filename + "?raw&at=" + $commitid
  $lineOut = $filename + "*" + $tempfile + "*" + $url + "`n"
  $lineOutBytes = $enc.GetBytes($lineOut)
  $tmpIni.write($lineOutBytes, 0, $lineOutBytes.Length)
} 

$lineOutBytes = $enc.GetBytes("SRCSRV: end")
$tmpIni.write($lineOutBytes, 0, $lineOutBytes.length)

$tmpIni.Close()

# Run the SDK tool to inject path info
ForEach($pdbfile in Get-ChildItem $srcDir\*.pdb)
{
  "c:\Program Files (x86)\Windows Kits\10\Debuggers\x64\srcsrv\pdbstr.exe -w -p:`"$pdbfile`" -s:srcsrv -i:$tmpfilename"
}

# And copy PDBs up.
$rand = Get-Random
$tmpDir = "\\applejack\incomingSymbols\" + $env:computername + $rand + $SrcDir.Replace("\", "_").Replace(":", "_").Replace(" ", "_")

New-Item $tmpDir -type Directory
echo $srcDir
Copy-Item $srcDir\*.pdb $tmpDir