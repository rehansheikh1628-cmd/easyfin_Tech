import { Component, ChangeDetectorRef } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from '../../services/auth.service';

@Component({
  selector: 'app-signup',
  templateUrl: './signup.component.html',
  styleUrls: ['./signup.component.css'],
  standalone: false
})
export class SignupComponent {
  fullName = '';
  email = '';
  organization = '';
  password = '';
  isLoading = false;
  errorMessage: string | null = null;

  constructor(
    private authService: AuthService,
    private router: Router,
    private cdr: ChangeDetectorRef
  ) {}

  onSignup(): void {
    if (!this.fullName || !this.email || !this.password) {
      this.errorMessage = 'Please provide full name, email, and password.';
      return;
    }

    if (this.password.length < 6) {
      this.errorMessage = 'Password must be at least 6 characters long.';
      return;
    }

    this.isLoading = true;
    this.errorMessage = null;
    this.cdr.markForCheck();

    this.authService.register({
      fullName: this.fullName.trim(),
      email: this.email.trim(),
      password: this.password,
      organization: this.organization ? this.organization.trim() : undefined
    }).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success) {
          this.router.navigate(['/dashboard']);
        } else {
          this.errorMessage = res.message || 'Registration failed.';
          this.cdr.markForCheck();
        }
      },
      error: (err) => {
        this.isLoading = false;
        this.errorMessage = err.error?.message || 'Registration failed. Please check your details.';
        this.cdr.markForCheck();
      }
    });
  }
}
