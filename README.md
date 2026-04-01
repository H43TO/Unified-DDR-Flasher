# Unified DDR SPD Flasher

A Windows desktop application for reading and writing SPD (Serial Presence Detect) EEPROMs on DDR3, DDR4, and DDR5 memory modules, as well as programming PMIC (Power Management IC) registers. It communicates with the RP2040‑based hardware programmer designed by @H43TO.

## Features

- **SPD Operations**
  - Read and write full SPD dumps (256/512/1024 bytes)
  - Auto‑detect module generation (DDR3, DDR4, DDR5)
  - Manage reversible write protection (RSWP) on DDR5 modules
  - Verify written data against original dump

- **PMIC Operations**
  - Read complete PMIC register map (256 bytes)
  - Read/write individual registers
  - Identify PMIC model and programming mode
  - Read voltages, currents, and power consumption
  - Unlock full access (manufacturer access) for some PMICs

## System Requirements

- Windows 7 or later (32/64‑bit)
- .NET Framework 4.7.2 or .NET Core 3.1 / .NET 5+ (depending on compilation)

## Getting Started

1. **Install the hardware**
   Connect your SPD programmer to a USB port. Windows should install the serial driver automatically (USB CDC). Note the COM port number that appears.

2. **Launch the application**
   Run `UnifiedDDRSPDFlasher.exe`.

3. **Connect**
   - In the *Flasher Configuration* tab, select the COM port from the list.
   - Click **Connect**. The status bar should turn green and show "Connected".

4. **Work with a module**
   - Go to the *SPD Operations* tab.
   - The application automatically scans for modules on the I2C bus.
   - Select a module from the dropdown.
   - Click **Read SPD** to fetch its contents.
   - You can save the dump to a file, load a previously saved dump, and write it back to the module.

5. **PMIC access**
   - Switch to the *PMIC Operations* tab.
   - The tab will scan for PMICs and show detected addresses.
   - Select a PMIC, then click **Read PMIC** to fetch its full register map.
   - Use the single register read/write or burn block controls to inspect or modify specific registers.

## Important Notes

- **Write protection**: For DDR5 modules, the reversible write protection blocks are shown as coloured squares. Clicking a square toggles protection for that block. Remember to clear write protection before trying to write a new SPD image.
- **DDR3 & DDR4 modules**: Most DDR3/4 modules use a simple 256/512‑byte SPD. The application can read and write them, but write protection is not supported.
- **PMIC writing**: The application currently supports reading PMICs and enabling full access. Writing vendor or block data is still under development; the buttons are present but only show a placeholder message.
- **Factory reset**: Resets the programmer’s internal settings (device name, I2C clock preference). This does *not* affect memory modules.

## Building from Source

1. Clone the repository.
2. Open the solution in Visual Studio (2019 or later).
3. Restore NuGet packages if any (none are used besides the base framework).
4. Build the project (Release or Debug).
5. The executable will be in `bin\Release\` or `bin\Debug\`.

## Credits

- Hardware & Frimware design by @H43TO

If you have questions or want to report issues, please open an issue on the GitHub repository.
