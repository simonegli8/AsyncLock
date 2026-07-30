SET PackageVersion=4.1.4
SET Configuration=Debug

del nupkg\*.nupkg
del nupkg\*.snupkg

dotnet pack AsyncLock.slnx -c %Configuration% ^
    -p:Version=%PackageVersion% ^
    -p:FileVersion=%PackageVersion% ^
    -p:AssemblyVersion=%PackageVersion%
    
REM dotnet pack AsyncLock.slnx -c %Configuration% ^
REM    -p:Version=%PackageVersion% ^
REM    -p:FileVersion=%PackageVersion% ^
REM    -p:AssemblyVersion=%PackageVersion% ^
REM    -p:DebugType=portable ^
REM    -p:DebugSymbols=true ^
REM    -p:IncludeSymbols=true ^
REM    -p:SymbolPackageFormat=snupkg