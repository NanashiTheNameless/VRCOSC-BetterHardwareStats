# Third-party notices

Nanashi's Better Hardware Stats Module is licensed under the GNU General Public License v3.0 only (see LICENSE).

## Used at runtime, not redistributed

| Component | Licence | How it is used |
|-----------|---------|----------------|
| VRCOSC and VolcanicArts.VRCOSC.SDK | GPL-3.0-only | Host application and module SDK |
| HWiNFO | Proprietary, separately installed (https://www.hwinfo.com/) | Reads the public shared-memory export of a running instance; no HWiNFO code, header, driver or SDK is shipped |
| Intel Graphics Control Library (IGCL) | Intel graphics driver license; public headers under MIT (https://github.com/intel/drivers.gpu.control-library) | Loads the driver-supplied ControlLib.dll; handwritten ABI declarations only, no Intel code or driver is shipped |
| NVIDIA NVML (nvml.dll) | NVIDIA driver licence | Loaded from the installed NVIDIA driver |
| AMD ADLX (amdadlx64.dll) | AMD driver licence; ADLX SDK headers under the MIT licence (https://github.com/GPUOpen-LibrariesAndSDKs/ADLX) | Loaded from the installed AMD driver; vtable slot indices and struct layouts follow the SDK headers |
| Windows APIs (pdh, dxgi, gdi32 D3DKMT, iphlpapi, kernel32) | Windows | Operating system |

Interop declarations were written by hand from the vendors' public headers; no header text is included.

## Redistributed unchanged

The release includes these Windows x64 assemblies. License texts and upstream notices are in `licenses/`. Upstream notice typography is transcribed to ASCII; original notices remain available in the linked source repositories.
LibreHardwareMonitorLib is pinned to a stable release and loaded only from the local `lhm/` bundle in an isolated AssemblyLoadContext. There is no fallback to the host's library.

| Component | License | Corresponding source |
|-----------|---------|----------------------|
| LibreHardwareMonitorLib | MPL-2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/3d331e3370efb858411f19511373eff65a218701 |
| DiskInfoToolkit | MPL-2.0, copyright Florian K. | https://github.com/Blacktempel/DiskInfoToolkit/tree/25319eae5781e75bcf141e844ceab2afe94d40ea |
| RAMSPDToolkit-NDD | MPL-2.0, copyright Florian K. | https://github.com/Blacktempel/RAMSPDToolkit/tree/3b47b960e0830fef344624ad5e389675d5f0a1ce |
| BlackSharp.Core | MPL-2.0, copyright Florian K. | https://github.com/Blacktempel/BlackSharp/tree/c70b735c6cec123ee8a046ac4a0bc6c606f52cf0 |
| HidSharp | Apache-2.0, copyright James F. Bellinger | https://github.com/IntergatedCircuits/HidSharp |
| Mono.Posix.NETStandard and Windows native helpers | MIT and upstream third-party licenses, copyright Microsoft and contributors | https://github.com/mono/mono |
| System.Management | MIT, copyright Microsoft and contributors | https://github.com/dotnet/dotnet/tree/44525024595742ebe09023abe709df51de65009b |
| System.IO.Ports | MIT, copyright Microsoft and contributors | https://github.com/dotnet/dotnet/tree/c2435c3e0f46de784341ac3ed62863ce77e117b4 |
| PawnIO.Modules, embedded by LHM | LGPL-2.1; see upstream notices | Complete source archive in `licenses/PawnIO.Modules-source.zip`; https://github.com/namazso/PawnIO.Modules/tree/4aa792beb020a14c8261072e9786d2dfb38489d9 |

The module does not install PawnIO. CPU sensors are off by default.
The MPL components are unmodified; their source is available at the exact revisions above.
