# Nanashi's Better Hardware Stats Module

An ~~improved~~ Opinionated replacement for VRCOSC's built-in Hardware Stats module, with added support for multiple GPUs, disk and network monitoring, and more sensor options.

## Support

If you find my projects useful, please consider supporting their maintenance. Support is optional.

- [GitHub Sponsors](https://github.com/sponsors/NanashiTheNameless)
- [Buy Me a Coffee](https://buymeacoffee.com/NamelessNanashi)
- [Ko-fi](https://ko-fi.com/NanashiTheNameless)
- [Liberapay](https://liberapay.com/NamelessNanashi)
- [Throne](https://throne.com/NamelessNanashi)

## Install

In VRCOSC, open Packages, click Refresh, search for Nanashi's Better Hardware Stats Module and click the green plus. Disable the official Hardware Stats module, enable this module and start VRCOSC.

For manual installation, close VRCOSC and extract a [release ZIP](https://github.com/NanashiTheNameless/VRCOSC-BetterHardwareStats/releases) into `%APPDATA%\VRCOSC\packages\local`. Keep the `lhm/` and `licenses/` folders. Use either the package installation or a local copy.

## Use

- Enable the features you need in General. Disabled features hide their settings and stop their workers.
- Choose devices from the dropdowns. Choices refresh when settings change and keep your selection. Each disk slot accepts Auto, a drive or None.
- Sampling defaults to 500 ms. Enter a number or use the arrows.
- Enable OSC in VRChat for avatar outputs. Parameter names and types match the official Hardware Stats module.
- Link the module to a ChatBox clip and use its default state or variables. For Pulse, connect its hardware source nodes to your graph.

GPU sensors use installed NVIDIA, AMD or Intel driver APIs, with the bundled LibreHardwareMonitorLib as a fallback. Availability depends on the hardware and drivers.

CPU sensors are off by default. LibreHardwareMonitor CPU sensors usually need administrator rights and an installed PawnIO driver. The module installs no driver. HWiNFO supports CPU temperature and power. Enable Shared Memory Support in HWiNFO, select HwInfo under CPU temperature and power, then choose the sensors from the dropdowns. Keep HWiNFO running; shared-memory availability and limits depend on its license.

When switching from Hardware Stats, update ChatBox module, state and variable references and replace Pulse hardware nodes. Back up your profile before unlinking the old module: VRCOSC removes its associated states and variables.

## Build

On Windows, install the .NET SDK selected by `global.json`. Close VRCOSC, run `tools\verify.cmd`, then reopen it. Debug builds copy the module to VRCOSC's local package folder.

## License

[GPL-3.0-only](LICENSE). Bundled dependencies are listed in [third-party notices](THIRD-PARTY-NOTICES.md), with their licenses and required sources in `licenses/`.
