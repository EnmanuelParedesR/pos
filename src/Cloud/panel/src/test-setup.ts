import { configure } from "@testing-library/react";

// TK-305: the complement to rendering pages without the whole App: on a loaded PC or CI runner a findBy waits up to 5 s, not 1 s.
configure({ asyncUtilTimeout: 5000 });
