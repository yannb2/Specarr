import re
import unittest

import bump_tools as b


class BumpToolsTests(unittest.TestCase):
    def tool(self, arg):
        return next(t for t in b.TOOLS if t.arg == arg)

    def test_versions_compare_as_numbers(self):
        self.assertEqual(b.newest(["8.1", "9.0", "10.0"]), "10.0")
        self.assertEqual(b.newest(["26.05", "26.10", "26.1"]), "26.10")
        self.assertEqual(b.newest(["2.3.4", "2.3.10"]), "2.3.10")

    def test_reads_and_sets_args(self):
        text = "ARG FFMPEG_VERSION=8.1\nARG DOVI_TOOL_VERSION=2.3.4\nFROM x\nARG FFMPEG_VERSION\n"
        self.assertEqual(b.read_args(text), {"FFMPEG_VERSION": "8.1", "DOVI_TOOL_VERSION": "2.3.4"})
        changed = b.set_arg(text, "FFMPEG_VERSION", "9.0")
        self.assertIn("ARG FFMPEG_VERSION=9.0\n", changed)
        self.assertIn("ARG FFMPEG_VERSION\n", changed)

    def test_sets_workflow_env(self):
        text = "env:\n    FFMPEG_VERSION: '8.1'\n    DOTNET_VERSION: '10.0.x'\n"
        changed = b.set_env(text, "FFMPEG_VERSION", "9.0")
        self.assertIn("    FFMPEG_VERSION: '9.0'\n", changed)
        self.assertIn("    DOTNET_VERSION: '10.0.x'\n", changed)

    def test_ffmpeg_reads_the_btbn_release_branches(self):
        names = ("ffmpeg-master-latest-linux64-gpl.tar.xz ffmpeg-n9.0-latest-linux64-gpl-9.0.tar.xz "
                 "ffmpeg-n9.0-latest-linux64-gpl-shared-9.0.tar.xz ffmpeg-n8.1-latest-linux64-gpl-8.1.tar.xz "
                 "ffmpeg-n9.0-latest-win64-gpl-9.0.zip")
        found = re.findall(self.tool("FFMPEG_VERSION").pattern, names)
        self.assertEqual(sorted(set(found)), ["8.1", "9.0"])
        self.assertEqual(b.newest(found), "9.0")

    def test_release_tags_of_another_kind_are_skipped(self):
        dovi = self.tool("DOVI_TOOL_VERSION")
        self.assertEqual(re.findall(dovi.pattern, "2.3.5"), ["2.3.5"])
        self.assertEqual(re.findall(dovi.pattern, "libdovi-3.4.0"), [])

    def test_every_tool_has_an_arg_in_the_dockerfile(self):
        pins = b.read_args(b.DOCKERFILE.read_text())
        for tool in b.TOOLS:
            self.assertIn(tool.arg, pins, tool.name)

    def test_other_pinned_files_match_the_dockerfile(self):
        pins = b.read_args(b.DOCKERFILE.read_text())
        for path in b.ALSO_PINNED_IN:
            text = (b.ROOT / path).read_text()
            for tool in b.TOOLS:
                match = re.search(rf"^\s+{tool.arg}:\s*['\"]?([^'\"\s]+)", text, re.MULTILINE)
                self.assertIsNotNone(match, f"{tool.arg} in {path}")
                self.assertEqual(match.group(1), pins[tool.arg], f"{tool.arg} in {path}")


if __name__ == "__main__":
    unittest.main()
