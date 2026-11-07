# PiPER acceleration feedback units

CAN `0x47C` carries a joint number in byte0 and an unsigned, big-endian16-bit
acceleration limit in bytes1–2. The decoded unit is **0.01 rad/s² per raw unit**.
Raw500 (`01 F4`) therefore reports5 rad/s²; raw501 reports5.01 rad/s².

The correction was reviewed against AgileX's official `pyAgxArm` source at
commit `d90e234ba8e408cfa964b38aa9120b3131fc9f85` on2026-11-05:

- [`Codec.decode_47C_motor_max_acc_limit`](https://github.com/agilexrobotics/pyAgxArm/blob/d90e234ba8e408cfa964b38aa9120b3131fc9f85/pyAgxArm/protocols/can_protocol/drivers/piper/default/parser.py)
  converts the unsigned raw value using `1e-2`.
- [`Driver.set_joint_acc_limits`](https://github.com/agilexrobotics/pyAgxArm/blob/d90e234ba8e408cfa964b38aa9120b3131fc9f85/pyAgxArm/protocols/can_protocol/drivers/piper/default/driver.py)
  documents0.01 rad/s² precision, converts a requested limit to raw with
  `round(abs(value) * 1e2)`, and independently checks the queried feedback with
  the same inverse conversion. The write message is `0x475`; no configuration
  write is needed to correct reception of `0x47C`.
- The [`v189` driver](https://github.com/agilexrobotics/pyAgxArm/blob/d90e234ba8e408cfa964b38aa9120b3131fc9f85/pyAgxArm/protocols/can_protocol/drivers/piper/versions/v189/driver.py)
  covers firmware at or above `S-V1.8-9` and inherits the
  [`v188` parser](https://github.com/agilexrobotics/pyAgxArm/blob/d90e234ba8e408cfa964b38aa9120b3131fc9f85/pyAgxArm/protocols/can_protocol/drivers/piper/versions/v188/parser.py).
  That parser inherits the default codec without an acceleration override.
  This includes the reviewed `S-V1.9-1` firmware. Earlier supported firmware
  follows the default driver; there is no corresponding acceleration-unit
  boundary in these implementations. The older0.001 comment in a feedback
  message class contradicts both executable conversions and is not used.

The separate `0x473` maximum-speed feedback does have a firmware-dependent
resolution. Its version checks remain unchanged. Likewise, decoding a numeric
acceleration report does not make unknown firmware eligible for motion.

The native controller still requires a valid acceleration report from the
current connection and rejects values outside `(0,5]` rad/s². It never clips an
out-of-policy report to5: for example raw501 remains5.01 and blocks admission.
The tests cover raw500 on every joint, unsigned/zero values, firmware ordering,
state JSON and rejection above the unchanged cap. No motion/configuration
commands are emitted by this decoder correction.
