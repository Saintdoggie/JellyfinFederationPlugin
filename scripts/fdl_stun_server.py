#!/usr/bin/env python3
"""Minimal RFC 5389 STUN Binding responder for the direct-link NAT harness.

Listens on several UDP ports (default 3478 and 3479) so a client can compare the
mapping a NAT gives it per destination. Replies with XOR-MAPPED-ADDRESS (IPv4).
"""
import socket
import struct
import sys
import selectors

MAGIC = 0x2112A442


def binding_response(data, addr):
    if len(data) < 20:
        return None
    mtype, length, cookie = struct.unpack('!HHI', data[:8])
    if mtype != 0x0001 or cookie != MAGIC or length != len(data) - 20:
        return None
    txid = data[8:20]
    ip = socket.inet_aton(addr[0])
    xport = addr[1] ^ (MAGIC >> 16)
    xip = bytes(a ^ b for a, b in zip(ip, struct.pack('!I', MAGIC)))
    attr = struct.pack('!HHBBH', 0x0020, 8, 0, 0x01, xport) + xip
    return struct.pack('!HHI', 0x0101, len(attr), MAGIC) + txid + attr


def main():
    ports = [int(p) for p in sys.argv[1:]] or [3478, 3479]
    sel = selectors.DefaultSelector()
    for port in ports:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.bind(('0.0.0.0', port))
        sel.register(s, selectors.EVENT_READ)
    print('stun listening on', ports, flush=True)
    while True:
        for key, _ in sel.select():
            data, addr = key.fileobj.recvfrom(2048)
            reply = binding_response(data, addr)
            if reply:
                key.fileobj.sendto(reply, addr)


if __name__ == '__main__':
    main()
