# Hex

The layout editor of [Horizon](https://github.com/bogzxo/horizon). Its own UI is a layout like the ones it edits,
drawn by the same code a game draws them with, so what is in the canvas is the real thing. Components out of a
palette, a tree of the layout, properties in sections that fold, the code it writes as it goes, undo, layers,
templates, the skin's art to pick from, and a self test that clicks through the lot.

```
git clone --recursive https://github.com/bogzxo/hex
dotnet build Hex.sln
Horizon.Hex/bin/Debug/net10.0/Horizon.Hex --selftest --exit --size 1600x900
```

The engine is a submodule at `horizon/` (on its `development`), with the language inside it. Work on
`development`, `master` is what's released.
