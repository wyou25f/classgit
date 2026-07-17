a = float(input("Первое число: "))
op = input("Операция (+ - * /): ")
b = float(input("Второе число: "))

if op == "+":
    print("Ответ:", a + b)
elif op == "-":
    print("Ответ:", a - b)
elif op == "*":
    print("Ответ:", a * b)
elif op == "/":
    if b != 0:
        print("Ответ:", a / b)
    else:
        print("Ошибка: деление на ноль!")
else:
    print("Неизвестная операция")
