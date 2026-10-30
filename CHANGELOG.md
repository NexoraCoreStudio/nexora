# Historique des versions

## 1.6.0 — 30 octobre 2026

- Nouvel installateur : thème clair, navigation bleu nuit, diagnostic détaillé et compte rendu exportable.
- Vérification SHA-256 du paquet complet avant installation et des fichiers après copie.
- Contrôle des droits d'écriture, de l'espace disque, des fichiers verrouillés et de l'installation existante ; refus des chemins redirigés et des collisions avec des fichiers non gérés.
- Prérequis USB/IP vérifiés au chemin utilisé par Nexora ; possibilité explicite de configurer USB/IP plus tard.
- Retour arrière renforcé, préservation du coffre et version d'installation cohérente.
- 17 scénarios automatisés pour l'installateur, intégrés au build coordonné et à la CI Windows.
- Inclut les correctifs de présence FIDO, de validation TLS, de certificats, d'écriture du coffre et de parsing USB/IP issus de l'audit du 30 octobre.

Les tests de connexion WebAuthn, de pilote, de redémarrage et d'intégration Windows complète restent à valider dans une VM dédiée avant une distribution publique.
