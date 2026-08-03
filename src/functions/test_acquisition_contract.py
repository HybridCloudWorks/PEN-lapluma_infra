import unittest

from acquisition_contract import propose_acquisition_batch, publish_acquisition_proposals


class AcquisitionContractTests(unittest.TestCase):
    def test_priority_set_is_exact_and_nothing_is_activated(self) -> None:
        proposals = propose_acquisition_batch(
            {
                "contractVersion": "0.2.0",
                "priorityForms": ["I-130", "I-485", "DS-11", "FAFSA"],
            }
        )
        self.assertEqual([item["formID"] for item in proposals], ["I-130", "I-485", "DS-11", "FAFSA"])
        self.assertTrue(all(item["status"] == "PROPOSED" for item in proposals))
        self.assertEqual(proposals[0]["authority"], "USCIS")
        self.assertEqual(proposals[-1]["artifactKind"], "EXTERNAL_WORKFLOW")
        self.assertEqual(proposals[-1]["fillCapability"], "REFERENCE_ONLY")
        self.assertFalse(publish_acquisition_proposals(proposals)["published"])

    def test_scope_change_fails_closed(self) -> None:
        with self.assertRaises(ValueError):
            propose_acquisition_batch(
                {"contractVersion": "0.2.0", "priorityForms": ["I-130", "N-400"]}
            )


if __name__ == "__main__":
    unittest.main()
