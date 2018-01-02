#!/bin/sh

baseURL=http://files.xd.lan:7990/projects/XD/repos/hypervisors/browse

cat tmp-pre.ini > tmp.ini

gitfiles=`git ls-files -s`
commitid=`git rev-parse HEAD`
echo "$gitfiles" | while read gitfile; do
  hash=`echo $gitfile | cut -b 8-47`
  filename=`echo $gitfile | cut -b 51-999`
  origsrc=`cygpath -w $(pwd)`/${filename}
  origsrc=`echo $origsrc | tr '/' '\\'`
  tempfile=$hash/${filename}
  url=${baseURL}/${filename}?raw&at=${commitid}
  echo $origsrc*$tempfile*$url >> tmp.ini
done 

echo SRCSRV: end >> tmp.ini

echo "`find -name \"*.pdb\"`" | while read file; do
"/cygdrive/c/Program Files (x86)/Windows Kits/10/Debuggers/x64/srcsrv/pdbstr.exe" -w -p:"$file" -s:srcsrv -i:tmp.ini

done