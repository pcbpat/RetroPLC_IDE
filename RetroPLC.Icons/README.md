# RetroPLC icon catalog

`RetroPLC.Icons` embeds the regular PNG files under `Win98SE/SE98` as Avalonia
resources and exposes them through the generated `Se98Icons` catalog. Symlink
aliases are intentionally omitted from the catalog and generated resource
manifest.

Regenerate the catalog from the repository root:

```shell
dotnet run --file RetroPLC.Icons/scripts/GenerateIconCatalog.cs -- RetroPLC.Icons
```

Use a generated icon from C#:

```csharp
button.SmallIcon = Se98Icons.Actions.Size16.DocumentSave;
```

Use the same icon from Avalonia XAML:

```xml
xmlns:icons="using:RetroPLC.Icons"

SmallIcon="{x:Static icons:Se98Icons.Actions.Size16.DocumentSave}"
```

The source icon theme and its license are under `Win98SE`.
