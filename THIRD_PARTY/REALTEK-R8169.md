# Realtek RTL810xE/R8169 driver work

The Realtek network support in `src/Cosmos.Kernel.HAL.X64/Devices/Network/`
is based on the publicly documented RTL8169/RTL810x register and descriptor
interfaces, and on the Linux kernel `r8169` driver:

- Linux source: <https://git.kernel.org/pub/scm/linux/kernel/git/torvalds/linux.git/tree/drivers/net/ethernet/realtek/r8169_main.c>
- Linux source license: GPL-2.0-only, preserved by the Linux kernel source.
- Hardware identification: Realtek PCI vendor `0x10ec`, RTL810xE device
  `0x8136`.

Cosmos-specific glue and new code remain clearly marked in the source. Any
code copied or adapted from the Linux driver must retain its original GPL-2.0
notice and is distributed under GPL-2.0-only. This notice is not a relicensing
of Linux code under Cosmos' BSD license.
