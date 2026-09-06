#include "shapes.h"

static int call_count;

/* TODO: memoize the result */
static int square(int v) { return v * v; }

int point_area(const Point *p) {
  call_count = MICRO_ADD(call_count, 1);
  return square(p->x) * square(p->y);
}

#ifdef CONFIG_MICRO_EXTRA
int point_extra(const Point *p) { return point_area(p); }
#else
int point_extra(const Point *p) { return 0; }
#endif
