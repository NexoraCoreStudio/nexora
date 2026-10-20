# Nexora Manager

Double-cliquez sur `Ouvrir-Nexora-Manager.cmd` sous Windows 11. Le Manager utilise Windows PowerShell 5.1 et Windows Forms.

- Exécution : démarrer Nexora depuis sa version de développement ou installée, arrêter explicitement le processus.
- Développement : compiler, exécuter les tests .NET 8.
- Publication : créer `Nexora.exe` et, avec Inno Setup 6, `Nexora-Setup.exe`.
- USB/IP : consulter les périphériques et ports connectés (lecture seule).
- Diagnostics et logs : vérifier les outils et consulter les journaux.
- Sauvegardes : exporter, avec avertissement, les données de Nexora dans un ZIP **non chiffré**.

La nouvelle installation ne récupère **aucune donnée antérieure**. Seul le processus `Nexora` et les chemins `Nexora` sont utilisés. Les clés cryptographiques peuvent se trouver à côté de l'exécutable dans `Nexora` : sauvegardez-les dans un volume chiffré.
