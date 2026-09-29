"""Shared number vectors: the same rows the Windows client's NumbersTests run (client/LANFax.Tests)."""
import json
import os
import unittest

from faxd.enqueue import check_number, normalize_number

HERE = os.path.dirname(os.path.abspath(__file__))
VECTORS = os.path.join(HERE, "numbers-vectors.json")
CLIENT_COPY = os.path.join(HERE, "..", "..", "client", "LANFax.Tests", "numbers-vectors.json")


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)["vectors"]


class NumberVectors(unittest.TestCase):
    def test_vectors(self):
        rows = load(VECTORS)
        self.assertGreaterEqual(len(rows), 20)
        for v in rows:
            with self.subTest(input=v["input"]):
                try:
                    n = normalize_number(v["input"])
                except ValueError as e:
                    # faxd rejects < 2 digits at normalization; the client does it in IsAllowed
                    self.assertFalse(v["allowed"])
                    self.assertLess(len(v["normalized"]), 2)
                    self.assertIn(v["reason"], str(e))
                    continue
                self.assertEqual(n, v["normalized"])
                if v["allowed"]:
                    self.assertEqual(check_number(n), n)
                else:
                    with self.assertRaises(ValueError) as cm:
                        check_number(n)
                    self.assertIn(v["reason"], str(cm.exception))

    @unittest.skipUnless(os.path.exists(CLIENT_COPY), "client tree not present (deployed server)")
    def test_client_copy_is_identical(self):
        self.assertEqual(load(VECTORS), load(CLIENT_COPY))


if __name__ == "__main__":
    unittest.main()
