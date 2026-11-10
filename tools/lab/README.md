# Lab checks

Checks for a Windows machine. `Test-WindowsTransport.ps1` is offline and
non-interactive, so a Windows CI runner with MinGW can run it; the USB setup
check stays manual.

- `Test-WindowsTransport.ps1` builds and runs the offline Windows transport
  fixtures (fake WinUSB, receive pump, hotplug, and the shared offline
  controller fixtures) through `make test-windows`. It needs MinGW `gcc`, `make`
  and a POSIX `sh` (Git for Windows) on `PATH`, and exits nonzero on any
  failure. It never opens an adapter or sends a robot command.
- `test_pika_usb_setup.ps1` checks the native-process wrapper in
  `tools/Setup-PikaUsb.ps1` under Windows PowerShell 5. It does not run setup,
  share USB or open a serial port.

Run them from any directory, for example
`powershell -NoProfile -File tools/lab/Test-WindowsTransport.ps1`.
