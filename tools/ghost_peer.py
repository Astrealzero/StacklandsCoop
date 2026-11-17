"""Silent fake peer: completes the handshake, then never sends anything again.

Used by selftest-timeout.ps1 to exercise the host's peer-timeout watchdog. A real
client sends a Ping every two seconds, so only a socket that goes quiet can prove
the host notices the silence itself.
"""
import socket
import struct
import sys
import time


def str_field(text):
    data = text.encode("utf-8")
    return struct.pack("<H", len(data)) + data


def hello(nick, mods_hash):
    payload = bytes([1])  # MsgType.Hello
    payload += struct.pack("<i", 1)
    payload += str_field("0.1.0")
    payload += str_field("dev-defs")
    payload += str_field(mods_hash)
    payload += str_field(nick)
    return struct.pack("<I", len(payload)) + payload


def main():
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 45941
    keep = float(sys.argv[2]) if len(sys.argv) > 2 else 30.0
    mods_hash = sys.argv[3] if len(sys.argv) > 3 else "61f7c3c0"

    sock = socket.create_connection(("127.0.0.1", port), timeout=5)
    sock.sendall(hello("Ghost", mods_hash))
    print("ghost peer connected on port", port, flush=True)

    deadline = time.time() + keep
    sock.settimeout(1.0)
    while time.time() < deadline:
        try:
            chunk = sock.recv(4096)
            if not chunk:
                print("host closed the connection", flush=True)
                return
        except socket.timeout:
            continue
        except OSError:
            return

    sock.close()
    print("ghost peer exiting", flush=True)


if __name__ == "__main__":
    main()
