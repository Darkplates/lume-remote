"""Lume's opaque TCP rendezvous relay. Python 3.10+, standard library only.

The relay pairs sockets. TLS is negotiated end to end by the Windows apps.
Never log room identifiers, invitations, screen contents, or session secrets.
"""
import argparse
import asyncio
import contextlib
import re
import signal
import socket

HEADER = re.compile(rb"LUME1 ([HV]) ([a-fA-F0-9]{32})\n")


class Relay:
    def __init__(self, max_connections=512):
        self.waiting = {}
        self.connections = 0
        self.max_connections = max_connections
        self.tasks = set()
        self.writers = set()

    async def handle(self, reader, writer):
        if self.connections >= self.max_connections:
            writer.write(b"\x03")
            with contextlib.suppress(Exception):
                await writer.drain()
            writer.close()
            return
        self.connections += 1
        self.writers.add(writer)
        task = asyncio.current_task()
        self.tasks.add(task)
        room = None
        entry = None
        raw_socket = writer.get_extra_info("socket")
        if raw_socket:
            raw_socket.setsockopt(socket.SOL_SOCKET, socket.SO_KEEPALIVE, 1)
            raw_socket.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        try:
            header = await asyncio.wait_for(reader.readuntil(b"\n"), 10)
            match = HEADER.fullmatch(header)
            if not match:
                writer.write(b"\x03")
                await writer.drain()
                return
            role, room = match.groups()
            room = room.lower()
            if role == b"H":
                if room in self.waiting:
                    writer.write(b"\x03")
                    await writer.drain()
                    return
                pair = asyncio.get_running_loop().create_future()
                entry = (reader, writer, pair)
                self.waiting[room] = entry
                writer.write(b"\x01")
                await writer.drain()
                # Waiting hosts send no TLS bytes until a viewer has paired.
                disconnected = asyncio.create_task(reader.read(1))
                try:
                    done, _ = await asyncio.wait([pair, disconnected], return_when=asyncio.FIRST_COMPLETED)
                    if disconnected in done:
                        return
                    disconnected.cancel()
                    with contextlib.suppress(asyncio.CancelledError):
                        await disconnected
                    peer_reader, peer_writer, released = pair.result()
                    try:
                        writer.write(b"\x01")
                        peer_writer.write(b"\x01")
                        await asyncio.gather(writer.drain(), peer_writer.drain())
                        forward = asyncio.create_task(self.pipe(reader, peer_writer))
                        backward = asyncio.create_task(self.pipe(peer_reader, writer))
                        try:
                            await asyncio.wait([forward, backward], return_when=asyncio.FIRST_COMPLETED)
                        finally:
                            forward.cancel()
                            backward.cancel()
                            await asyncio.gather(forward, backward, return_exceptions=True)
                    finally:
                        if not released.done():
                            released.set_result(None)
                finally:
                    disconnected.cancel()
                    with contextlib.suppress(asyncio.CancelledError):
                        await disconnected
                    if pair.done() and not pair.cancelled():
                        _, _, released = pair.result()
                        if not released.done():
                            released.set_result(None)
            else:
                waiting = self.waiting.pop(room, None)
                if waiting is None or waiting[2].done():
                    writer.write(b"\x02")
                    await writer.drain()
                    return
                released = asyncio.get_running_loop().create_future()
                waiting[2].set_result((reader, writer, released))
                await released
        except (asyncio.IncompleteReadError, asyncio.LimitOverrunError, TimeoutError, ConnectionError, OSError):
            pass
        finally:
            if room is not None and entry is not None and self.waiting.get(room) is entry:
                self.waiting.pop(room, None)
            self.connections -= 1
            self.writers.discard(writer)
            self.tasks.discard(task)
            writer.close()
            with contextlib.suppress(Exception):
                await writer.wait_closed()

    @staticmethod
    async def pipe(reader, writer):
        while data := await reader.read(65536):
            writer.write(data)
            await writer.drain()

    async def close(self):
        for writer in list(self.writers):
            writer.close()
        for task in list(self.tasks):
            task.cancel()
        await asyncio.gather(*list(self.tasks), return_exceptions=True)


async def serve(args):
    relay = Relay(args.max_connections)
    server = await asyncio.start_server(relay.handle, args.bind, args.port, limit=128)
    print(f"Lume relay listening on {args.bind}:{args.port}. End-to-end TLS stays inside the paired stream.", flush=True)
    try:
        async with server:
            await server.serve_forever()
    finally:
        await relay.close()


def main():
    parser = argparse.ArgumentParser(description="Self-hosted Lume TCP relay. No third-party dependencies.")
    parser.add_argument("--bind", default="127.0.0.1", help="Default is local-only. Use 0.0.0.0 explicitly for a reachable server.")
    parser.add_argument("--port", type=int, default=24817)
    parser.add_argument("--max-connections", type=int, default=512, help="Operational resource guard, not a licence limit.")
    args = parser.parse_args()
    if not 1 <= args.port <= 65535 or args.max_connections < 2:
        parser.error("Invalid port or connection capacity")
    try:
        asyncio.run(serve(args))
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
