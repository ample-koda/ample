"""Seed handoff tests without loading speech models or using a GPU."""
import importlib.util
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location("voice_seed_worker", Path(__file__).with_name("worker.py"))
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


class Seeds(unittest.TestCase):
    def test_all_rngs_are_reset_including_cuda_and_large_seeds(self):
        numpy = SimpleNamespace(random=SimpleNamespace(seed=Mock()))
        torch = SimpleNamespace(manual_seed=Mock(), cuda=SimpleNamespace(is_available=lambda: True, manual_seed_all=Mock()), backends=SimpleNamespace(cudnn=SimpleNamespace(benchmark=True, deterministic=False)), use_deterministic_algorithms=Mock())
        seed = 2 ** 63 + 17
        with patch.dict("sys.modules", {"numpy": numpy, "torch": torch}):
            worker.seed_generators(seed)
            worker.seed_generators(seed)
        numpy.random.seed.assert_called_with(17)
        torch.manual_seed.assert_called_with(seed)
        torch.cuda.manual_seed_all.assert_called_with(seed)
        self.assertFalse(torch.backends.cudnn.benchmark)
        self.assertTrue(torch.backends.cudnn.deterministic)
        self.assertEqual(torch.manual_seed.call_count, 2)


if __name__ == "__main__":
    unittest.main()
