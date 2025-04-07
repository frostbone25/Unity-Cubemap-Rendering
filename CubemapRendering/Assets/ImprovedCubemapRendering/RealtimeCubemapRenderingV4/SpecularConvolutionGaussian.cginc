#define PI 3.14159265359f

float GaussianWeight(int x, int y, int radius)
{
    float sigma = radius / 2.0;
    float coeff = 1.0 / (pow(2.0 * PI * sigma * sigma, 1.5));
    float exponent = -(x * x + y * y) / (2.0 * sigma * sigma);
    return coeff * exp(exponent);
}
