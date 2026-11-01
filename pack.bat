SET PackageVersion=4.2.0
SET Configuration=Release

del nupkg\*.nupkg
del nupkg\*.snupkg

dotnet pack AsyncLock.slnx -c %Configuration% ^
    -p:Version=%PackageVersion% ^
    -p:FileVersion=%PackageVersion% ^
    -p:AssemblyVersion=%PackageVersion%
    
dotnet pack AsyncLock.slnx -c %Configuration% ^
    -p:Version=%PackageVersion% ^
    -p:FileVersion=%PackageVersion% ^
    -p:AssemblyVersion=%PackageVersion% ^
    -p:DebugType=portable ^
    -p:DebugSymbols=true ^
    -p:IncludeSymbols=true ^
    -p:SymbolPackageFormat=snupkg