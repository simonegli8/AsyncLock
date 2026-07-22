SET PackageVersion=4.1.3
SET Configuration=Debug

del nupkg\*.nupkg
del nupkg\*.snupkg

dotnet pack AsyncLock.slnx -c %Configuration% -p:Version=%PackageVersion% -p:FileVersion=%PackageVersion% -p:AssemblyVersion=%PackageVersion%