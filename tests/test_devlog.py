"""The devlog builds, every post has its header, and every picture it shows exists."""
import os
import re
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "scripts"))
import build_devlog  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), "..")


def test_devlog_builds(tmp_path):
    posts = build_devlog.read_posts()
    assert len(posts) >= 10 and len({p["slug"] for p in posts}) == len(posts)
    page = open(build_devlog.build(str(tmp_path / "devlog")), encoding="utf-8").read()
    for src in re.findall(r'<img src="([^"]+)"', page):
        if src.startswith("../showcase/"):
            assert os.path.exists(os.path.join(ROOT, "showcase", src[len("../showcase/"):])), src
        else:
            assert (tmp_path / "devlog" / src).exists(), src
