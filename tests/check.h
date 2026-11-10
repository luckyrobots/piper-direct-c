#ifndef PIPER_TEST_CHECK_H
#define PIPER_TEST_CHECK_H
#include <stdio.h>
#include <stdlib.h>
/* Always evaluates expr, even under -DNDEBUG, so side effects such as
   mkdtemp() or controller_init() can never be compiled out. */
#define CHECK(expr) do { if (!(expr)) { fprintf(stderr,"%s:%d: CHECK failed: %s\n",__FILE__,__LINE__,#expr); abort(); } } while (0)
#endif
