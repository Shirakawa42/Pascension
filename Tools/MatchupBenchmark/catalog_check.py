"""A separate host build may renumber IL metadata tokens, not input features."""
import copy

def compatible_catalog(expected,actual):
    left,right=copy.deepcopy(expected),copy.deepcopy(actual)
    # Coverage hashes contain raw method IL, including build-specific metadata
    # tokens. Require identical method coverage and every semantic field/table.
    a=left['card_feature_descriptor']['coverage'].pop('methodChecksums')
    b=right['card_feature_descriptor']['coverage'].pop('methodChecksums')
    if set(a)!=set(b) or left!=right:
        raise ValueError('Benchmark changed semantic card catalog, observations, actions or method coverage')
    return dict(semantic_catalog_identical=True,coverage_method_names_identical=True,build_local_il_hashes_changed=sum(a[k]!=b[k] for k in a))
