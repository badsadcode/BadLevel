"""Space types: every room kind has one, and new kinds plug in without touching the core."""
from badlevel import GenConfig, generate
from badlevel.graph import MissionGraph
from badlevel.spaces import REGISTRY, SpaceType, register, space_for, vertical_kinds
from badlevel.spaces.room import ROLES, RoomSpace
from badlevel.validate import validate


def test_every_room_kind_has_a_space_type():
    for kind in ROLES + ("stairwell", "lift", "ladder", "spiral"):
        assert isinstance(space_for(kind), SpaceType)
    assert set(vertical_kinds()) == {"stairwell", "lift", "ladder", "spiral"}
    for lv in (generate(seed=3), generate(GenConfig(seed=3, storeys=3))):
        for room in lv.rooms:
            assert room.kind in REGISTRY


def test_a_new_space_type_plugs_in_without_touching_the_core():
    """A 'shrine' version of the secret room: always square, with a raised dais in the middle.
    Written outside the core, registered, used by the normal pipeline, then removed."""

    class Shrine(RoomSpace):
        name = "shrine"
        kinds = ("secret",)

        def assign_shape(self, room, node, cfg, rng, merged):
            room.shape = "shrine"            # keep the rectangle, but say what it is

        def decorate(self, level, room, ctx, rng):
            cx, cy = room.x + room.w // 2, room.y + room.h // 2
            for c in ((cx, cy), (cx - 1, cy), (cx, cy - 1), (cx - 1, cy - 1)):
                ctx.set_floor(c, room.floor + 16)
            room.features.append("dais")

    original = REGISTRY["secret"]
    try:
        register(Shrine())
        lv = generate(seed=5)
        secret = next(r for r in lv.rooms if r.kind == "secret")
        assert secret.shape == "shrine" and "dais" in secret.features
        cx, cy = secret.x + secret.w // 2, secret.y + secret.h // 2
        assert lv.room_spans((cx, cy), secret)[0][0] == secret.floor + 16
        assert validate(lv)["ok"]
    finally:
        register(original)
    assert space_for("secret") is original
