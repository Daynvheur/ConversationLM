# Référence des fonctionnalités C# (Mise à jour Septembre 2026)

Ce fichier sert de base de connaissance locale pour les versions récentes de C#.

## C# 14 (.NET 10)

### Syntaxe et Productivité
- **Mot-clé `field`** : Accès direct au champ de recul synthétisé. 
  - *Exemple* : `public string Name { get; set => field = value; }`
- **Null-conditional assignment** : Affectation conditionnelle avec `?.`.
  - *Exemple* : `obj?.Property = value;`
- **Extension Members** : Extension de types via des propriétés ou membres statiques dans un bloc `extension`.
- **Lambda avec modificateurs** : Ajout de `ref`, `in`, `out`, `scoped` directement aux paramètres de lambda.

### Performance et Types
- **Conversions Span implicites** : Meilleure interopérabilité entre `Span<T>`, `ReadOnlySpan<T>` et `T[]`.
- **`nameof` unbound generics** : `nameof(List<>)` -> `List`.

---

## C# 15 (Preview - .NET 11)

### Types Avancés
- **Union Types** : Représentation d'une valeur pouvant être l'un de plusieurs types.
  - *Exemple* : `public union Pet(Cat, Dog);`
- **Closed Hierarchies** : Utilisation du modificateur `closed` pour limiter l'héritage et aider le compiler sur l'exhaustivité du pattern matching.
- **Extension Indexers** : Indexeurs définis dans un bloc `extension`.

### Contrôle de flux et Collections
- **Collection Expression Arguments** : Paramétrage des collections via `with`.
  - *Exemple* : `List<int> list = [with(capacity: 10), 1, 2, 3];`
- **Labeled `break` / `continue`** : Sortie de boucles imbriquées ciblées.
  - *Exemple* : `break outerLoop;`

### Sécurité Mémoire
- **Refonte de l'unsafe** : Déplacement de la responsabilité de sécurité vers les opérations d'accès réel à la mémoire. Relaxation des règles pour la déclaration de pointeurs et l'utilisation de `fixed` hors contexte `unsafe`.
