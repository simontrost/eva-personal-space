"""Keep Piper's phoneme sequence consistent with each model's vocabulary.

Older voices such as Ramona lack some combining marks emitted by current
eSpeak. Piper already skips these when producing model IDs, but its alignment
builder still expects their padding IDs. This breaks lip-sync even though
the model generated valid audio. Filter the same unsupported symbols before
both steps, without changing the IDs sent to the voice model.
"""

from dataclasses import fields

from piper import PiperVoice


class EVAPiperVoice(PiperVoice):
    @staticmethod
    def load(*args, **kwargs) -> "EVAPiperVoice":
        voice = PiperVoice.load(*args, **kwargs)
        return EVAPiperVoice(
            **{field.name: getattr(voice, field.name) for field in fields(PiperVoice)}
        )

    def phonemize(self, text: str) -> list[list[str]]:
        return [
            [
                phoneme for phoneme in sentence
                if phoneme in self.config.phoneme_id_map
            ]
            for sentence in super().phonemize(text)
        ]
