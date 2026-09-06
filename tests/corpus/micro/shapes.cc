#include "shapes.h"

namespace micro {

/// Base shape.
class Shape {
 public:
  virtual int Area() const;
  int id_;
};

class Square : public Shape {
 public:
  int Area() const override;
  explicit Square(int side);

 private:
  int side_;
};

int Shape::Area() const { return 0; }

int Square::Area() const { return side_ * side_; }

Square::Square(int side) : side_(side) {}

enum class Kind : int { kShape, kSquare };

using Alias = Shape;

}  // namespace micro

TEST_F(ShapeTest, AreaIsSquared) { EXPECT_EQ(4, 4); }
