# Firmware Artefacts — Unified DDR Flasher

## Files

- `UDF_fw.uf2` — drag-and-drop UF2 image for the RP2040 in BOOTSEL mode.
- `UDF_fw.bin` — raw binary, for flashing via picotool / SWD.

## Flashing

1. Hold the **BOOTSEL** button on the RP2040 board while plugging it in via USB.
2. The board enumerates as a mass-storage drive named **RPI-RP2**.
3. Drag `UDF_fw.uf2` onto that drive. It will reboot automatically.
4. After reboot, the board enumerates as a USB CDC serial port (`COMx` on
   Windows). The status LED is steady-on once `stdio_usb_init()` completes
   and the firmware is idle.