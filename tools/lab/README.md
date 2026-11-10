# Lab checks

Manual checks for a Windows lab machine. CI does not run them.

- `Test-WindowsTransport.ps1` builds and runs the offline Windows transport
  fixtures (fake WinUSB, receive pump, hotplug, and the shared offline
  controller fixtures through the Makefile). It needs MinGW `gcc` and `make`
  on `PATH`. It never opens an adapter or sends a robot command.
- `test_pika_usb_setup.ps1` checks the native-process wrapper in
  `tools/Setup-PikaUsb.ps1` under Windows PowerShell 5. It does not run setup,
  share USB or open a serial port.

Run them from any directory, for example
`powershell -NoProfile -File tools/lab/Test-WindowsTransport.ps1`.
