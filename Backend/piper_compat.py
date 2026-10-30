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
