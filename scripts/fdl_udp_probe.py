#!/usr/bin/env python3
"""Send UDP datagrams and report whether anything comes back (harness helper).

usage: fdl_udp_probe.py host port hexpayload [hexpayload ...]
Prints one line per payload: "<n> reply <hex>" or "<n> silent".
"""
import socket
import sys

host, port = sys.argv[1], int(sys.argv[2])
for i, hexs in enumerate(sys.argv[3:]):
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.settimeout(2)
    s.sendto(bytes.fromhex(hexs), (host, port))
    try:
        data, _ = s.recvfrom(4096)
        print(i, 'reply', data.hex())
    except socket.timeout:
        print(i, 'silent')
