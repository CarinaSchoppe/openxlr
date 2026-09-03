"""Fold pw-dump's initial array and any final change batches by registry id."""
import json


def parse_dump(text):
    try:
        snapshot = json.loads(text)
    except json.JSONDecodeError:
        decoder = json.JSONDecoder()
        objects = {}
        remainder = text.lstrip()
        if not remainder:
            raise ValueError("Empty PipeWire dump")
        while remainder:
            batch, end = decoder.raw_decode(remainder)
            if not isinstance(batch, list):
                raise ValueError("Expected a PipeWire object array")
            for item in batch:
                if not isinstance(item, dict) or type(item.get("id")) is not int or not 0 <= item["id"] <= 0xffffffff:
                    raise ValueError("PipeWire update has no valid registry id")
                if ("info" in item and item["info"] is None) or ("props" in item and item["props"] is None):
                    objects.pop(item["id"], None)
                else:
                    objects[item["id"]] = item
            remainder = remainder[end:].lstrip()
        snapshot = list(objects.values())
    if not isinstance(snapshot, list):
        raise ValueError("Expected a PipeWire object array")
    return snapshot
