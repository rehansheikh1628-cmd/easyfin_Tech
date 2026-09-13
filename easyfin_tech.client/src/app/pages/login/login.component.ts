import { Component, ChangeDetectorRef, OnInit } from '@angular/core';
import { Router, ActivatedRoute } from '@angular/router';
import { AuthService } from '../../services/auth.service';

@Component({
  selector: 'app-login',
  templateUrl: './login.component.html',
  styleUrls: ['./login.component.css'],
  standalone: false
})
export class LoginComponent implements OnInit {
  email = '';
  password = '';
  rememberMe = true;
  isLoading = false;
  errorMessage: string | null = null;
  showPassword = false;
  showForgotNotice = false;
  private returnUrl = '/dashboard';

  constructor(
    private authService: AuthService,
    private router: Router,
    private route: ActivatedRoute,
    private cdr: ChangeDetectorRef
  ) {}

  ngOnInit(): void {
    this.returnUrl = this.route.snapshot.queryParams['returnUrl'] || '/dashboard';
    // If already authenticated, redirect straight to returnUrl
    if (this.authService.isAuthenticated) {
      this.router.navigateByUrl(this.returnUrl);
      return;
    }

    // Verify session on cold load
    this.authService.checkAuth().subscribe((user) => {
      if (user) {
        this.router.navigateByUrl(this.returnUrl);
      }
    });
  }

  togglePasswordVisibility(): void {
    this.showPassword = !this.showPassword;
  }

  toggleForgotNotice(): void {
    this.showForgotNotice = !this.showForgotNotice;
  }

  onLogin(): void {
    if (!this.email || !this.password) {
      this.errorMessage = 'Please enter both email and password.';
      return;
    }

    this.isLoading = true;
    this.errorMessage = null;
    this.cdr.markForCheck();

    this.authService.login({
      email: this.email.trim(),
      password: this.password,
      rememberMe: this.rememberMe
    }).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success) {
          this.router.navigateByUrl(this.returnUrl);
        } else {
          this.errorMessage = res.message || 'Login failed. Please check your credentials.';
          this.cdr.markForCheck();
        }
      },
      error: (err) => {
        this.isLoading = false;
        this.errorMessage = err.error?.message || 'Invalid email or password. Please try again.';
        this.cdr.markForCheck();
      }
    });
  }
}
