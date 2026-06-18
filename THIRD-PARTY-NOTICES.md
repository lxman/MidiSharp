# Third-Party Notices

MidiSharp includes third-party software, used and redistributed under the terms below.

## NVorbis

The Ogg Vorbis decoder in `MidiSharp.Audio.Vorbis` (assembly `MidiSharp.Audio.Vorbis.dll`,
sources under `vendor/NVorbis/`) is a **maintained fork of [NVorbis](https://github.com/NVorbis/NVorbis)
v0.10.5**, originally written by **Andrew Ward**.

The decoder logic is the upstream source; only the namespace and assembly name were re-identified
(from `NVorbis` to `MidiSharp.Audio.Vorbis`) so this fork cannot collide with the upstream NVorbis
NuGet package. NVorbis is no longer actively maintained upstream; this fork is maintained as part of
MidiSharp.

NVorbis is licensed under the MIT License. The original copyright and license are retained verbatim in
[`vendor/NVorbis/LICENSE`](vendor/NVorbis/LICENSE):

> Copyright (c) 2020 Andrew Ward
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
> associated documentation files (the "Software"), to deal in the Software without restriction … (full
> text in `vendor/NVorbis/LICENSE`).
