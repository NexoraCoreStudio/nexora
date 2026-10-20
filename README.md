# nexora — Nexora 1.1
Application Windows Avalonia (Nexora.exe) et Nexora Manager.

**Dépendances locales** : dossiers frères `nexora-fido` et `nexora-usb`.

```powershell
dotnet build .\Nexora.sln -c Release
dotnet run --project .\Nexora.Gui\Nexora.Gui.csproj -c Release
```

`Nexora.Manager/Ouvrir-Nexora-Manager.cmd` ouvre l'outil Windows de commandes. Le moteur FIDO2 et USB/IP n'est pas dupliqué ici.
