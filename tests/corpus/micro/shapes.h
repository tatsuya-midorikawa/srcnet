#ifndef MICRO_SHAPES_H
#define MICRO_SHAPES_H

#include <stddef.h>
#include "common.h"

#define MICRO_MAX 16
#define MICRO_ADD(a, b) ((a) + (b))

/** A point in the plane. */
typedef struct Point {
  int x;
  int y;
} Point;

enum Color { COLOR_RED, COLOR_GREEN };

int point_area(const Point *p);

#ifdef CONFIG_MICRO_EXTRA
int point_extra(const Point *p);
#endif

#endif
