"""Bounded, latest-snapshot recognition while a PCM recording is arriving."""
import asyncio
import logging
import time


class LiveTranscription:
    def __init__(self, session_id, sample_rate, language, transcribe, send, interval=0.65):
        self.session_id = session_id
        self.sample_rate = sample_rate
        self.language = language
        self.transcribe = transcribe
        self.send = send
        self.interval = interval
        self.pending = None
        self.changed = asyncio.Event()
        self.closed = False
        self.last_offer = 0.0
        self.last_text = ""
        self.revision = 0
        self.task = asyncio.create_task(self._run())

    def offer(self, pcm):
        now = time.monotonic()
        if self.closed or len(pcm) < self.sample_rate or now - self.last_offer < self.interval:
            return
        # At most one in-flight decode and one pending snapshot. Slow GPUs never
        # accumulate a queue of outdated recognition jobs.
        self.pending = bytes(pcm)
        self.last_offer = now
        self.changed.set()

    async def _run(self):
        try:
            while not self.closed:
                await self.changed.wait()
                self.changed.clear()
                pcm, self.pending = self.pending, None
                text = await asyncio.to_thread(self.transcribe, pcm, self.sample_rate, self.language)
                if self.closed:
                    return
                text = text.strip()
                if text and text != self.last_text:
                    self.revision += 1
                    await self.send({"type": "transcription.partial", "sessionId": self.session_id,
                                     "text": text, "language": self.language,
                                     "revision": self.revision})
                    self.last_text = text
        except asyncio.CancelledError:
            raise
        except Exception:
            self.closed = True
            # A preview failure must not stop the final full-quality transcription.
            logging.getLogger("eva.backend").warning("Live transcription stopped", exc_info=True)

    async def close(self):
        self.closed = True
        self.pending = None
        self.task.cancel()
        try:
            await self.task
        except asyncio.CancelledError:
            pass
