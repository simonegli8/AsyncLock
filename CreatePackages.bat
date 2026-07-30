SET PackageVersion=4.1.4
SET Configuration=Debug

del nupkg\*.nupkg
del nupkg\*.snupkg

dotnet pack AsyncLock.slnx -c %Configuration% ^
    -p:Version=%PackageVersion% ^
    -p:FileVersion=%PackageVersion% ^
    -p:AssemblyVersion=%PackageVersion% ^
    -p:DebugType=embedded


dotnet pack AsyncLock.slnx -c %Configuration% ^
    -p:Version=%PackageVersion% ^
    -p:FileVersion=%PackageVersion% ^
    -p:AssemblyVersion=%PackageVersion% ^
    -p:DebugType=portable ^
    -p:DebugSymbols=true ^
    -p:IncludeSymbols=true ^
    -p:SymbolPackageFormat=snupkg