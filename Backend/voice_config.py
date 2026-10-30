"""Bilingual voice profiles selected in EVA's menu.

Runtime, model downloads and preflight use the same configuration.
Without a menu choice Unity can use the appearance. Older clients default to female.
"""

PIPER_ENGLISH_VOICE_NAME = "en_US-kristin-medium"
PIPER_GERMAN_VOICE_NAME = "de_DE-ramona-low"
PIPER_MALE_ENGLISH_VOICE_NAME = "en_US-mike-medium"
PIPER_MALE_GERMAN_VOICE_NAME = "de_DE-thorsten-high"

PIPER_VOICE_PROFILES = {
    "female": {"en": PIPER_ENGLISH_VOICE_NAME, "de": PIPER_GERMAN_VOICE_NAME},
    "male": {"en": PIPER_MALE_ENGLISH_VOICE_NAME, "de": PIPER_MALE_GERMAN_VOICE_NAME},
}

PIPER_VOICE_NAMES = (
    PIPER_ENGLISH_VOICE_NAME,
    PIPER_GERMAN_VOICE_NAME,
    PIPER_MALE_ENGLISH_VOICE_NAME,
    PIPER_MALE_GERMAN_VOICE_NAME,
)


def normalize_voice_profile(profile: str | None = None) -> str:
    if profile is None or profile == "":
        return "female"
    if not isinstance(profile, str) or profile.lower() not in PIPER_VOICE_PROFILES:
        raise ValueError("voiceProfile must be 'female' or 'male'.")
    return profile.lower()


def repository_path(voice_name: str, extension: str) -> str:
    locale, speaker, quality = voice_name.split("-")
    language = locale.split("_")[0]
    return f"{language}/{locale}/{speaker}/{quality}/{voice_name}{extension}"


PIPER_FILES = {
    f"{voice_name}{extension}": repository_path(voice_name, extension)
    for voice_name in PIPER_VOICE_NAMES
    for extension in (".onnx", ".onnx.json")
}
