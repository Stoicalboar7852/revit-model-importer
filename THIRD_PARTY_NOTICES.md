# Third-party notices

Revit Model Importer is released under the MIT License (see `LICENSE`). It ships, adapts or uses the following
third-party components. All of them are under permissive licenses (MIT, BSD-3-Clause, Unlicense). No Autodesk
binaries are redistributed: the Revit API is referenced at build time only (see "Build-time only" below).

| Component | Version | Used by | Licence | Shipped |
|---|---|---|---|---|
| [AssimpNetter](https://github.com/Saalvage/AssimpNetter) (fork of AssimpNet) | 6.0.5 | Worker | MIT | yes |
| [Open Asset Import Library (Assimp)](https://github.com/assimp/assimp) native library | 6.0 (bundled by AssimpNetter) | Worker | BSD-3-Clause (+ bundled contrib, see below) | yes |
| [StbImageSharp](https://github.com/StbSharp/StbImageSharp) | 2.30.16 | Worker | Unlicense OR MIT (used under MIT) | yes |
| [Pfim](https://github.com/nickbabcock/Pfim) | 0.11.4 | Worker | MIT | yes |
| [Fast-Quadric-Mesh-Simplification](https://github.com/sp4cerat/Fast-Quadric-Mesh-Simplification) | algorithm structure adapted in `QuadricSimplifier.cs` | Core | MIT | adapted source |
| OKLab colour space formulas (Björn Ottosson) | — | Core | public domain / MIT | formulas only |
| Nice3point.Revit.Api.RevitAPI / RevitAPIUI | 2025.0.2, 2026.0.4, 2027.0.20 | add-in build | see package | **no** (reference assemblies, compile time only) |
| xUnit, Microsoft.NET.Test.Sdk | 2.9.3 / 17.14.1 | tests | Apache-2.0 / MIT | no |

---

## AssimpNetter

Copyright (c) 2012-2020 AssimpNet - Nicholas Woodfield
Copyright (c) 2024-2026 AssimpNetter - Stefan Koch
 
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.
 
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.

## Open Asset Import Library (assimp)

Open Asset Import Library (assimp)

Copyright (c) 2006-2026, assimp team
All rights reserved.

Redistribution and use of this software in source and binary forms,
with or without modification, are permitted provided that the
following conditions are met:

* Redistributions of source code must retain the above
  copyright notice, this list of conditions and the
  following disclaimer.

* Redistributions in binary form must reproduce the above
  copyright notice, this list of conditions and the
  following disclaimer in the documentation and/or other
  materials provided with the distribution.

* Neither the name of the assimp team, nor the names of its
  contributors may be used to endorse or promote products
  derived from this software without specific prior
  written permission of the assimp team.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The native Assimp library also contains third-party code under its own permissive licences (for example
Poly2Tri, Clipper, zlib/minizip, pugixml, RapidJSON, utf8cpp, OpenDDL-Parser, Open3DGC). Their licences are in
the Assimp source tree under `contrib/` (<https://github.com/assimp/assimp/tree/master/contrib>). Poly2Tri's notice,
as included in Assimp's LICENSE:

Poly2Tri Copyright (c) 2009-2010, Poly2Tri Contributors
http://code.google.com/p/poly2tri/

All rights reserved.
Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice,
  this list of conditions and the following disclaimer.
* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.
* Neither the name of Poly2Tri nor the names of its contributors may be
  used to endorse or promote products derived from this software without specific
  prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## StbImageSharp

StbImageSharp is a C# port of Sean Barrett's stb_image. It is dual-licensed under the Unlicense (public domain)
or the MIT License; Revit Model Importer uses it under the MIT License:

Copyright (c) StbImageSharp contributors; stb_image by Sean Barrett.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Pfim

Copyright (c) Nick Babcock

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Fast-Quadric-Mesh-Simplification

`src/RevitModelImporter.Core/Simplification/QuadricSimplifier.cs` follows the iteration scheme of this project
(threshold schedule, cached per-triangle edge errors, reference lists, flip test, barycentric UV update), rewritten
in C# with the changes listed at the top of that file.

Copyright © 2015-2019 Spacerat and contributors
(Mesh Simplification Tutorial, (C) by Sven Forstmann in 2014, License: MIT)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## OKLab

The OKLab conversion matrices in `PaletteQuantizer.cs` are Björn Ottosson's published formulas
(<https://bottosson.github.io/posts/oklab/>), which he placed in the public domain (also offered under MIT).

## Build-time only: Revit API reference assemblies

The add-in compiles against `Nice3point.Revit.Api.RevitAPI` and `Nice3point.Revit.Api.RevitAPIUI`, which contain
reference assemblies and API documentation for Autodesk Revit. They are referenced with `ExcludeAssets="runtime"`
and `PrivateAssets="all"`; nothing from them is copied into the build output or the packages
(`packaging/Build-Package.ps1` fails if an Autodesk assembly is found). Autodesk and Revit are trademarks of
Autodesk, Inc. This project is not affiliated with or endorsed by Autodesk.
