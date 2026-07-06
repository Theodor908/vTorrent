# Security Policy

## Supported Versions

vTorrent is under active development. Security fixes are applied to the latest
released version and the `main` branch.

| Version        | Supported          |
| -------------- | ------------------ |
| Latest release | :white_check_mark: |
| Older releases | :x:                |

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub issues,
discussions, or pull requests.**

Instead, report them privately through GitHub Security Advisories:

1. Go to the [**Security** tab](https://github.com/Theodor908/vTorrent/security)
   of this repository.
2. Click **"Report a vulnerability"**.
3. Provide a detailed description.

### What to include

- The type of issue and the affected component (e.g. peer protocol, DHT,
  tracker client, file I/O).
- Steps to reproduce, or a proof-of-concept.
- The potential impact (e.g. remote code execution, path traversal, data
  exfiltration, denial of service).
- Any suggested mitigation, if you have one.

### What to expect

- An acknowledgment of your report as soon as is practical.
- An assessment and, where confirmed, a coordinated fix and disclosure.
- Credit for the discovery, if you would like it.

Because vTorrent is a peer-to-peer network application that parses untrusted
input from the network and from `.torrent` files, reports concerning input
parsing, memory safety, and path handling are especially valued.
