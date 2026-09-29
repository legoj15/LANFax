# LANFax Windows client

Build everything (tests, app publish, installer when Inno Setup 6 is installed):

    pwsh client/build.ps1

Run only the tests:

    dotnet test client/LANFax.sln

Requires the .NET SDK version pinned in `global.json`. The installer is unsigned, so Windows SmartScreen
warns on first run.

The printer is installed and removed by `install/Install-LANFaxPrinter.ps1` and
`install/Uninstall-LANFaxPrinter.ps1` (each asks for administrator approval).
