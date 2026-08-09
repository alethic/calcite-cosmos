# Logo

The crystal is Apache Calcite's own, by way of the [.NET Calcite mark](https://github.com/alethic/calcite-dotnet) —
facets, white edges, sparkles and all, in the .NET palette. Around it is an orbit: an ellipse
tilted 22°, passing behind the crystal's point and crossing in front of its base. That is the
Cosmos half of the name.

| file | use |
|---|---|
| `icon.svg` | crystal and orbit on the .NET badge — package icon, avatar, favicon |
| `icon.png` | 512×512 raster of `icon.svg`; the copy at the repository root is the `PackageIcon` |
| `mark.svg` | crystal and orbit alone, 512×512 |
| `mono.svg` | single-colour silhouette; change the one `fill` to recolour it |

There is deliberately no wordmark lockup. A mark setting "APACHE" and "calcite" in Calcite's own
arrangement reads as an Apache Software Foundation product, and this repository is not one.

The square canvases keep a safe area: the artwork's longer side is 80% of the box, except
`icon.svg`, whose badge is edge to edge by design.

## Geometry

On the 512 canvas the crystal is 300 tall, centred on (260, 244). The orbit is centred on
(252, 294) with radii 198 × 78, rotated −22°, stroked at 15. The whole ellipse is drawn under the
crystal in the darker of its two tones; the lower half is drawn again over the crystal in the
lighter one, so the ring reads as passing behind the point and in front of the base.

`mono.svg` has only one colour to work with, so the same ring is drawn once, behind a dilated copy
of the crystal's silhouette that clears a 9-unit gap around it. The break is what carries the
occlusion.

## Colour

The crystal keeps the two .NET remappings of Calcite's six tones — a blue-to-purple ramp for the
free-standing mark, and a light one for the crystal sitting on the purple badge.

| Calcite | mark | on badge |
|---|---|---|
| `#ffffff` | `#FFFFFF` | `#FFFFFF` |
| `#d5e5ff` | `#E4E4FF` | `#F5F1FF` |
| `#aaccff` | `#A6C6FF` | `#E3DAFF` |
| `#80b3ff` | `#86A8FF` | `#CBBBF9` |
| `#5599ff` | `#6E5EEA` | `#AE97F4` |
| `#2a7fff` | `#512BD4` | `#8869EC` |

The orbit is Azure blue, in two tones per variant — near half light, far half dark:

| | near | far |
|---|---|---|
| `mark.svg` | `#29B6E8` | `#1B7FC4` |
| `icon.svg` | `#50E6FF` | `#2E63C8` |

The badge is `#512BD4`, the .NET purple.

## Provenance

The crystal is a derivative of the Apache Calcite logo, Copyright the Apache Software Foundation,
used under the Apache License 2.0; each SVG carries that notice. The orbit is original to this
repository — it is not the Azure Cosmos DB icon and borrows no part of it. This is a community mark
for this repository: not an ASF mark, not a Microsoft one, and no endorsement by either is implied.
